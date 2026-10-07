using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Xml;
using System.Security.Cryptography;

namespace BmsSerialDemo
{
    public sealed class StoreStatus
    {
        public string Source { get; internal set; }
        public string DatabasePath { get; internal set; }
        public long CommittedSamples { get; internal set; }
        public long TotalSamples { get; internal set; }
        public int QueuedCommands { get; internal set; }
        public int PendingCount { get { return QueuedCommands; } }
        public bool RecordingEnabled { get; internal set; }
        public bool IsRecording { get { return RecordingEnabled; } }
        public string LastError { get; internal set; }
        public long AvailableDiskBytes { get; internal set; }
        public bool DiskWarning { get; internal set; }
    }
    public sealed class XlsxExportProgress { public string Phase { get; internal set; } public long CompletedRows { get; internal set; } public long TotalRows { get; internal set; } }
    public sealed class XlsxExportResult { public string OutputDirectory { get; internal set; } public int FileCount { get; internal set; } public long SampleRows { get; internal set; } public long AlarmRows { get; internal set; } public long PolicyRows { get; internal set; } public IList<string> Files { get; internal set; } }
    public sealed class StoredSample
    {
        public string Database { get; internal set; }
        public long Id { get; internal set; }
        public string Source { get; internal set; }
        public string HistorySessionId { get; internal set; }
        public long AcquisitionRound { get; internal set; }
        public int? PeriodSeconds { get; internal set; }
        public DateTime ReceivedUtc { get; internal set; }
        public byte Address { get; internal set; }
        public int Pack { get; internal set; }
        public int Soc { get; internal set; }
        public int Soh { get; internal set; }
        public int Cycles { get; internal set; }
        public int? BalanceLowRaw { get; internal set; }
        public int? BalanceHighRaw { get; internal set; }
        public int? HumidityPercent { get; internal set; }
        public double Voltage { get; internal set; }
        public double Current { get; internal set; }
        public double RemainingAh { get; internal set; }
        public double TotalAh { get; internal set; }
        public int VoltageCentivolts { get { return (int)Math.Round(Voltage * 100); } }
        public int CurrentCentiamps { get { return (int)Math.Round(Current * 100); } }
        public int[] Cells { get; internal set; }
        public int[] Temperatures { get; internal set; }
    }

    // Single writer per source DB. Queue saturation or disk errors disable recording and are surfaced in Status.
    public sealed partial class SampleStore : IDisposable
    {
        internal static readonly SemaphoreSlim ExportGate = new SemaphoreSlim(1,1);
        const int QueueLimit = 1024, BatchLimit = 128, QueueBytesLimit = 16 * 1024 * 1024;
        sealed class Work
        {
            public Action<SQLiteConnection, SQLiteTransaction> Execute;
            public TaskCompletionSource<object> Completion;
            public bool Sample;
            public int PayloadBytes;
        }
        readonly BlockingCollection<Work> queue = new BlockingCollection<Work>(QueueLimit);
        readonly object stateLock = new object();
        readonly string source, dbPath, connectionString;
        readonly Thread worker;
        readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        volatile bool enabled = true, stopping;
        string error = "";
        long committed;
        long totalSamples;
        int queuedBytes;
        long availableDiskBytes; int diskWarning;
        int sessionId;
        public Task Ready { get { return ready.Task; } }
        public string Source { get { return source; } }
        public string DatabasePath { get { return dbPath; } }
        public SampleStore(string dataRoot, string source)
            : this(dataRoot, source, true) { }
        public SampleStore(string dataRoot, string source, bool createIfMissing)
        {
            this.source = source;
            if (source != "simulation" && source != "serial") throw new ArgumentException("source 必须为 simulation 或 serial");
            if(createIfMissing)Directory.CreateDirectory(dataRoot); dbPath = Path.Combine(dataRoot, source + ".db");
            if(!createIfMissing&&!File.Exists(dbPath))throw new FileNotFoundException("该来源尚无本地数据库",dbPath);
            connectionString = "Data Source=" + dbPath + ";Version=3;Journal Mode=Delete;SyncMode=Full;BusyTimeout=5000;";
            worker = new Thread(WorkerLoop); worker.IsBackground = true; worker.Name = "BMS SQLite writer " + source; worker.Start();
        }
        readonly bool readOnlyDatabase;
        public SampleStore(string source, string databasePath, bool createIfMissing, bool readOnlyCompatibility)
        {
            if (source != "simulation" && source != "serial") throw new ArgumentException("source 必须为 simulation 或 serial");
            this.source = source; dbPath = Path.GetFullPath(databasePath);readOnlyDatabase=readOnlyCompatibility;
            if(createIfMissing)Directory.CreateDirectory(Path.GetDirectoryName(dbPath));
            if(!createIfMissing&&!File.Exists(dbPath))throw new FileNotFoundException("该来源尚无本地数据库",dbPath);
            connectionString = "Data Source=" + dbPath + ";Version=3;"+(readOnlyDatabase?"Read Only=True;":"Journal Mode=Delete;SyncMode=Full;")+"BusyTimeout=5000;";
            worker = new Thread(WorkerLoop); worker.IsBackground = true; worker.Name = "BMS SQLite writer " + source; worker.Start();
        }
        public async Task<int> StartSessionAsync(byte address, string label)
        {
            await Ready.ConfigureAwait(false); int id = 0;
            await Submit(delegate(SQLiteConnection c, SQLiteTransaction t) {
                using (SQLiteCommand cmd = new SQLiteCommand("INSERT INTO sessions(source,address,started_utc_ms,label) VALUES(@s,@a,@t,@l); SELECT last_insert_rowid();", c, t)) {
                    cmd.Parameters.AddWithValue("@s", source); cmd.Parameters.AddWithValue("@a", (int)address); cmd.Parameters.AddWithValue("@t", UtcNowMs()); cmd.Parameters.AddWithValue("@l", label ?? ""); id = Convert.ToInt32(cmd.ExecuteScalar());
                }
            }, false).ConfigureAwait(false);
            sessionId = id; return id;
        }
        public Task SetRecordingPeriodAsync(int seconds) { return RecordPolicyChangeAsync(seconds); }
        public Task RecordPolicyChangeAsync(int seconds)
        {
            if(seconds<1||seconds>86400)throw new ArgumentOutOfRangeException("seconds");
            int sid=sessionId; if(sid<=0)return FailedTask(new InvalidOperationException("没有活动记录会话"));
            return Submit(delegate(SQLiteConnection c,SQLiteTransaction t){using(SQLiteCommand cmd=new SQLiteCommand("INSERT INTO recording_policies(session_id,changed_utc_ms,period_seconds) VALUES(@s,@t,@p)",c,t)){cmd.Parameters.AddWithValue("@s",sid);cmd.Parameters.AddWithValue("@t",UtcNowMs());cmd.Parameters.AddWithValue("@p",seconds);cmd.ExecuteNonQuery();}},false);
        }
        public async Task StopSessionAsync()
        {
            int id = sessionId; if (id <= 0) return;
            await Submit(delegate(SQLiteConnection c, SQLiteTransaction t) {
                using (SQLiteCommand cmd = new SQLiteCommand("UPDATE sessions SET ended_utc_ms=@t WHERE id=@id AND ended_utc_ms IS NULL", c, t)) { cmd.Parameters.AddWithValue("@t", UtcNowMs()); cmd.Parameters.AddWithValue("@id", id); cmd.ExecuteNonQuery(); }
            }, false).ConfigureAwait(false);
            if(sessionId==id)sessionId=0;
        }
        public bool TryEnqueue(RealtimeSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Source != source || sessionId <= 0 || !enabled || stopping) return false;
            int sid = sessionId;
            int bytes=128+snapshot.RawFrame.Length+snapshot.Payload.Length+snapshot.AlarmPayload.Length+snapshot.Cells.Count*4+snapshot.Temperatures.Count*4;
            return EnqueueBounded(new Work { Sample = true, PayloadBytes=bytes, Execute = delegate(SQLiteConnection c, SQLiteTransaction t) { InsertSample(c, t, snapshot, sid); } });
        }
        public bool TryEnqueue(AlarmSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Source != source || sessionId <= 0 || !enabled || stopping) return false;
            int sid = sessionId;
            Work w = new Work { PayloadBytes=128+snapshot.RawFrame.Length+snapshot.Payload.Length+snapshot.DecodedText.Length*2, Execute = delegate(SQLiteConnection c, SQLiteTransaction t) {
                using (SQLiteCommand cmd = new SQLiteCommand("INSERT INTO alarm_observations(session_id,source,address,pack,received_utc_ms,decoded_text,raw_frame,payload,acquisition_round,period_seconds) VALUES(@sid,@s,@a,@p,@tm,@d,@r,@b,@round,@period)", c, t)) {
                    cmd.Parameters.AddWithValue("@sid", sid); cmd.Parameters.AddWithValue("@s", source); cmd.Parameters.AddWithValue("@a", (int)snapshot.Address); cmd.Parameters.AddWithValue("@p", snapshot.Pack); cmd.Parameters.AddWithValue("@tm", ToMs(snapshot.ReceivedUtc)); cmd.Parameters.AddWithValue("@d", snapshot.DecodedText); cmd.Parameters.Add("@r", DbType.Binary).Value = snapshot.RawFrame; cmd.Parameters.Add("@b", DbType.Binary).Value = snapshot.Payload;cmd.Parameters.AddWithValue("@round",snapshot.AcquisitionRound>0?(object)snapshot.AcquisitionRound:DBNull.Value);cmd.Parameters.AddWithValue("@period",snapshot.PeriodSeconds.HasValue?(object)snapshot.PeriodSeconds.Value:DBNull.Value); cmd.ExecuteNonQuery();
                }
            } };
            return EnqueueBounded(w);
        }
        bool EnqueueBounded(Work w)
        {
            int next=Interlocked.Add(ref queuedBytes,w.PayloadBytes);
            if(next>QueueBytesLimit){Interlocked.Add(ref queuedBytes,-w.PayloadBytes);Fail("存储待写载荷超过16 MiB；已停止记录，数据未入库。（触发来源：采样入队）");return false;}
            try{if(queue.TryAdd(w))return true;}
            catch(ObjectDisposedException){/* 与 Dispose 竞态：存储正在关闭，丢弃该样本即可，不计为记录故障。 */}
            Interlocked.Add(ref queuedBytes,-w.PayloadBytes);
            if(!stopping)Fail("存储队列已满（1024）；已停止记录，数据未入库。（触发来源：采样入队）");
            return false;
        }
        static void InsertSample(SQLiteConnection c, SQLiteTransaction t, RealtimeSnapshot s, int sid)
        {
            long sampleId;
            using (SQLiteCommand cmd = new SQLiteCommand("INSERT INTO samples(session_id,source,address,pack,received_utc_ms,voltage_cV,current_cA,soc,soh,remaining_cAh,total_cAh,cycles,balance_low_raw,balance_high_raw,humidity_pct,raw_frame,payload,alarm_payload,acquisition_round,period_seconds) VALUES(@sid,@s,@a,@p,@tm,@v,@i,@soc,@soh,@ra,@ta,@cy,@bl,@bh,@hu,@raw,@pl,@al,@round,@period); SELECT last_insert_rowid();", c, t)) {
                cmd.Parameters.AddWithValue("@sid", sid); cmd.Parameters.AddWithValue("@s", s.Source); cmd.Parameters.AddWithValue("@a", (int)s.Address); cmd.Parameters.AddWithValue("@p", s.Pack); cmd.Parameters.AddWithValue("@tm", ToMs(s.ReceivedUtc)); cmd.Parameters.AddWithValue("@v", (int)Math.Round(s.Voltage * 100)); cmd.Parameters.AddWithValue("@i", (int)Math.Round(s.Current * 100)); cmd.Parameters.AddWithValue("@soc", s.Soc); cmd.Parameters.AddWithValue("@soh", s.Soh); cmd.Parameters.AddWithValue("@ra", (int)Math.Round(s.RemainingAh * 100)); cmd.Parameters.AddWithValue("@ta", (int)Math.Round(s.TotalAh * 100)); cmd.Parameters.AddWithValue("@cy", s.Cycles); cmd.Parameters.AddWithValue("@bl",s.BalanceLow);cmd.Parameters.AddWithValue("@bh",s.BalanceHigh);cmd.Parameters.AddWithValue("@hu",s.Humidity); cmd.Parameters.Add("@raw", DbType.Binary).Value = s.RawFrame; cmd.Parameters.Add("@pl", DbType.Binary).Value = s.Payload; cmd.Parameters.Add("@al", DbType.Binary).Value = s.AlarmPayload; cmd.Parameters.AddWithValue("@round",s.AcquisitionRound>0?(object)s.AcquisitionRound:DBNull.Value);cmd.Parameters.AddWithValue("@period",s.PeriodSeconds.HasValue?(object)s.PeriodSeconds.Value:DBNull.Value); sampleId = Convert.ToInt64(cmd.ExecuteScalar());
            }
            int[] cells = s.CellArray, temps = s.TemperatureArray;
            using (SQLiteCommand cmd = new SQLiteCommand("INSERT INTO cell_values(sample_id,cell_index,millivolts) VALUES(@id,@n,@v)", c, t)) { SQLiteParameter id=cmd.Parameters.Add("@id",DbType.Int64), n=cmd.Parameters.Add("@n",DbType.Int32), v=cmd.Parameters.Add("@v",DbType.Int32); for(int i=0;i<cells.Length;i++){id.Value=sampleId;n.Value=i+1;v.Value=cells[i];cmd.ExecuteNonQuery();} }
            using (SQLiteCommand cmd = new SQLiteCommand("INSERT INTO temperature_values(sample_id,measurement_index,celsius) VALUES(@id,@n,@v)", c, t)) { SQLiteParameter id=cmd.Parameters.Add("@id",DbType.Int64), n=cmd.Parameters.Add("@n",DbType.Int32), v=cmd.Parameters.Add("@v",DbType.Int32); for(int i=0;i<temps.Length;i++){id.Value=sampleId;n.Value=i+1;v.Value=temps[i];cmd.ExecuteNonQuery();} }
        }
        public StoreStatus GetStatus() { lock(stateLock) { int queued; try{queued=queue.Count;}catch(ObjectDisposedException){queued=0;} return new StoreStatus { Source=source,DatabasePath=dbPath,CommittedSamples=Interlocked.Read(ref committed),TotalSamples=Interlocked.Read(ref totalSamples),QueuedCommands=queued,RecordingEnabled=enabled&&sessionId>0&&!stopping,LastError=error,AvailableDiskBytes=Interlocked.Read(ref availableDiskBytes),DiskWarning=Volatile.Read(ref diskWarning)!=0 }; } }
        public async Task<IList<StoredSample>> QueryPageAsync(DateTime fromUtc, DateTime toUtc, int pack, long beforeId, int limit)
        {
            if(limit<1||limit>500) throw new ArgumentOutOfRangeException("limit","limit 必须为 1..500");
            List<StoredSample> rows=new List<StoredSample>(); await Submit(delegate(SQLiteConnection c,SQLiteTransaction t) {
                using(SQLiteCommand cmd=new SQLiteCommand("SELECT id,received_utc_ms,address,pack,soc,soh,cycles,voltage_cV,current_cA,remaining_cAh,total_cAh,balance_low_raw,balance_high_raw,humidity_pct FROM samples WHERE received_utc_ms>=@f AND received_utc_ms<@to AND (@p=0 OR pack=@p) AND (@before=0 OR id<@before) ORDER BY id DESC LIMIT @n",c,t)) {
                    cmd.Parameters.AddWithValue("@f",ToMs(fromUtc));cmd.Parameters.AddWithValue("@to",ToMs(toUtc));cmd.Parameters.AddWithValue("@p",pack);cmd.Parameters.AddWithValue("@before",beforeId);cmd.Parameters.AddWithValue("@n",limit);
                    using(SQLiteDataReader r=cmd.ExecuteReader()) while(r.Read()) rows.Add(new StoredSample { Id=r.GetInt64(0),ReceivedUtc=FromMs(r.GetInt64(1)),Address=(byte)r.GetInt32(2),Pack=r.GetInt32(3),Soc=r.GetInt32(4),Soh=r.GetInt32(5),Cycles=r.GetInt32(6),Voltage=r.GetInt32(7)/100.0,Current=r.GetInt32(8)/100.0,RemainingAh=r.GetInt32(9)/100.0,TotalAh=r.GetInt32(10)/100.0,BalanceLowRaw=r.IsDBNull(11)?(int?)null:r.GetInt32(11),BalanceHighRaw=r.IsDBNull(12)?(int?)null:r.GetInt32(12),HumidityPercent=r.IsDBNull(13)?(int?)null:r.GetInt32(13),Cells=new int[0],Temperatures=new int[0] });
                }
                foreach(StoredSample row in rows) { row.Cells=ReadValues(c,t,"cell_values","cell_index","millivolts",row.Id); row.Temperatures=ReadValues(c,t,"temperature_values","measurement_index","celsius",row.Id); }
            },false).ConfigureAwait(false); return rows;
        }
        static int[] ReadValues(SQLiteConnection c,SQLiteTransaction t,string table,string order,string value,long id) { List<int> a=new List<int>(); using(SQLiteCommand cmd=new SQLiteCommand("SELECT "+value+" FROM "+table+" WHERE sample_id=@id ORDER BY "+order,c,t)){cmd.Parameters.AddWithValue("@id",id);using(SQLiteDataReader r=cmd.ExecuteReader())while(r.Read())a.Add(r.GetInt32(0));}return a.ToArray(); }
        public Task<long> ExportCsvAsync(string path, DateTime fromUtc, DateTime toUtc, int pack) { return ExportCsvAsync(path,fromUtc,toUtc,pack,CancellationToken.None); }
        public string GetSuggestedCsvFileName(DateTime fromUtc, DateTime toUtc, int pack) { return ExportNaming.CsvSuggestion(source,pack,fromUtc.ToUniversalTime(),toUtc.ToUniversalTime()); }
        public Task<long> ExportCsvAsync(string path,DateTime fromUtc,DateTime toUtc,int pack,CancellationToken cancellationToken){return ExportCsvAsync(path,fromUtc,toUtc,pack,cancellationToken,null);}
        public async Task<long> ExportCsvAsync(string path, DateTime fromUtc, DateTime toUtc, int pack, CancellationToken cancellationToken,IProgress<XlsxExportProgress> progress)
        {
            ExportNaming.VerifyFileDestination(path);
            if(toUtc<=fromUtc)throw new ArgumentException("结束时间必须晚于开始时间");
            if(!await ExportGate.WaitAsync(0,cancellationToken).ConfigureAwait(false))throw new InvalidOperationException("已有CSV或XLSX导出任务正在进行，请稍后重试。");
            try { long upper=0; await Submit(delegate(SQLiteConnection c,SQLiteTransaction t){using(SQLiteCommand cmd=new SQLiteCommand("SELECT COALESCE(MAX(id),0) FROM samples",c,t)) upper=Convert.ToInt64(cmd.ExecuteScalar());},false).ConfigureAwait(false);
            return await Task.Run(delegate { return ExportCsvWorker(path,fromUtc,toUtc,pack,upper,cancellationToken,progress); }).ConfigureAwait(false); }
            finally { ExportGate.Release(); }
        }
        long ExportCsvWorker(string path,DateTime fromUtc,DateTime toUtc,int pack,long upper,CancellationToken cancellationToken,IProgress<XlsxExportProgress> progress)
        {
            if(String.IsNullOrWhiteSpace(path))throw new ArgumentException("请选择CSV目标路径","path");if(File.Exists(path)||Directory.Exists(path))throw new IOException("目标文件已存在，未覆盖："+path);string outRoot=Path.GetPathRoot(Path.GetFullPath(path));if(new DriveInfo(outRoot).AvailableFreeSpace<100L*1024*1024)throw new IOException("CSV输出卷可用空间低于100 MiB；导出未开始。");string workDir=ExportNaming.CreateWorkDirectory();string partial=Path.Combine(workDir,Path.GetFileName(path)+".partial");long count=0;
            string readConnection="Data Source="+dbPath+";Version=3;Read Only=True;BusyTimeout=5000;";
            try {
            using(SQLiteConnection c=new SQLiteConnection(readConnection)){c.Open();using(StreamWriter w=new StreamWriter(partial,false,new UTF8Encoding(true))) {
                w.WriteLine("local_time,voltage_V,current_A,soc_pct,soh_pct,remaining_Ah,total_Ah,cycles,balance_low_raw,balance_high_raw,humidity_pct,raw_frame_hex,payload_hex,alarm_payload_hex");
                long after=0; while(true) { cancellationToken.ThrowIfCancellationRequested(); List<string[]> raw=new List<string[]>();
                    using(SQLiteCommand cmd=new SQLiteCommand("SELECT id,received_utc_ms,address,pack,soc,soh,cycles,voltage_cV,current_cA,remaining_cAh,total_cAh,balance_low_raw,balance_high_raw,humidity_pct,raw_frame,payload,alarm_payload FROM samples WHERE id>@after AND id<=@upper AND received_utc_ms>=@f AND received_utc_ms<@to AND (@p=0 OR pack=@p) ORDER BY id LIMIT 200",c)) {
                        cmd.Parameters.AddWithValue("@after",after);cmd.Parameters.AddWithValue("@upper",upper);cmd.Parameters.AddWithValue("@f",ToMs(fromUtc));cmd.Parameters.AddWithValue("@to",ToMs(toUtc));cmd.Parameters.AddWithValue("@p",pack);
                        using(SQLiteDataReader r=cmd.ExecuteReader()) while(r.Read()){ long id=r.GetInt64(0); after=id;DateTime utc=FromMs(r.GetInt64(1));raw.Add(new string[]{id.ToString(CultureInfo.InvariantCulture),utc.ToString("o",CultureInfo.InvariantCulture),r.GetInt32(2).ToString(CultureInfo.InvariantCulture),r.GetInt32(3).ToString(CultureInfo.InvariantCulture),(r.GetInt32(7)/100.0).ToString("0.00",CultureInfo.InvariantCulture),(r.GetInt32(8)/100.0).ToString("0.00",CultureInfo.InvariantCulture),r.GetInt32(4).ToString(CultureInfo.InvariantCulture),r.GetInt32(5).ToString(CultureInfo.InvariantCulture),(r.GetInt32(9)/100.0).ToString("0.00",CultureInfo.InvariantCulture),(r.GetInt32(10)/100.0).ToString("0.00",CultureInfo.InvariantCulture),r.GetInt32(6).ToString(CultureInfo.InvariantCulture),r.IsDBNull(11)?"":r.GetInt32(11).ToString(CultureInfo.InvariantCulture),r.IsDBNull(12)?"":r.GetInt32(12).ToString(CultureInfo.InvariantCulture),r.IsDBNull(13)?"":r.GetInt32(13).ToString(CultureInfo.InvariantCulture),r.IsDBNull(14)?"":Hex((byte[])r[14]),r.IsDBNull(15)?"":Hex((byte[])r[15]),r.IsDBNull(16)?"":Hex((byte[])r[16]),utc.ToLocalTime().ToString("o",CultureInfo.InvariantCulture)}); }
                    }
                    if(raw.Count==0)break;
                    foreach(string[] row in raw) { cancellationToken.ThrowIfCancellationRequested(); string[] cols={row[17],row[4],row[5],row[6],row[7],row[8],row[9],row[10],row[11],row[12],row[13],row[14],row[15],row[16]}; w.WriteLine(String.Join(",",Array.ConvertAll(cols,Csv))); count++; }
                    if(progress!=null)progress.Report(new XlsxExportProgress{Phase="正在导出CSV",CompletedRows=count,TotalRows=0});
                }
            }}
            if(count==0)throw new InvalidOperationException("所选时间范围和 Pack 没有可导出的实时数据。");if(File.Exists(path)||Directory.Exists(path))throw new IOException("目标文件已存在，未覆盖："+path);ExportNaming.PublishFile(partial,path,cancellationToken);return count;
            } finally { ExportNaming.TryDeleteDirectory(workDir); }
        }
        static string Csv(string s){return "\""+(s??"").Replace("\"","\"\"")+"\"";} static string Csv(int[] a){return String.Join(";",Array.ConvertAll(a,x=>x.ToString(CultureInfo.InvariantCulture)));} static string Hex(byte[] b){return BitConverter.ToString(b).Replace("-","");}
        Task Submit(Action<SQLiteConnection,SQLiteTransaction> action,bool sample)
        {
            if(readOnlyDatabase)return FailedTask(new InvalidOperationException("只读兼容库不处理排队命令；查询与导出请使用目录层跨库只读通道。"));
            if(!enabled||stopping)return FailedTask(new IOException(String.IsNullOrEmpty(error)?"Store is not accepting writes":error));
            TaskCompletionSource<object> tcs=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously); Work w=new Work{Execute=action,Completion=tcs,Sample=sample};
            try{if(!queue.TryAdd(w)){if(sample){Fail("存储队列已满（1024）；已停止记录。（触发来源：样本写入命令）");return FailedTask(new IOException(error));}return FailedTask(new IOException("存储队列已满（1024）；本次查询/导出/会话命令未执行，记录功能未受影响，请稍后重试。"));}}
            catch(ObjectDisposedException){return FailedTask(new IOException("存储已关闭，无法写入"));}
            catch(InvalidOperationException){return FailedTask(new IOException("存储已关闭，无法写入"));}
            return tcs.Task;
        }
        static Task FailedTask(Exception ex) { TaskCompletionSource<object> t=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously); t.SetException(ex); return t.Task; }
        void WorkerLoop()
        {
            Mutex writerMutex=null;bool owns=false;
            try { if(!readOnlyDatabase){writerMutex=new Mutex(false,@"Global\BmsSerialDemo.Store."+MutexPathKey(dbPath));try{owns=writerMutex.WaitOne(0);}catch(AbandonedMutexException){owns=true;}if(!owns)throw new IOException("同一个本地数据库路径已由另一个BMS采集进程打开；为避免并发写入，本实例拒绝记录。");}CheckDiskSpace(true);
              using(SQLiteConnection c=new SQLiteConnection(connectionString)){c.Open();if(!readOnlyDatabase)Init(c);using(SQLiteCommand countCmd=new SQLiteCommand("SELECT COUNT(*) FROM samples",c))totalSamples=TableExists(c,"samples")?Convert.ToInt64(countCmd.ExecuteScalar()):0;ready.TrySetResult(true);
                // A compatibility/query/export store has no queued writes. Close its sole read handle
                // after status initialization instead of retaining one idle SQLite thread per volume.
                if(readOnlyDatabase)return;
                DateTime lastDiskCheck=DateTime.UtcNow;
                while(!stopping||queue.Count>0){Work first;try{if(!queue.TryTake(out first,100))continue;}catch{break;}Interlocked.Add(ref queuedBytes,-first.PayloadBytes);List<Work> batch=new List<Work>{first};while(batch.Count<BatchLimit){Work next;if(!queue.TryTake(out next))break;Interlocked.Add(ref queuedBytes,-next.PayloadBytes);batch.Add(next);}SQLiteTransaction tx=null;
                    try{if((DateTime.UtcNow-lastDiskCheck).TotalSeconds>=30){CheckDiskSpace(false);lastDiskCheck=DateTime.UtcNow;}tx=c.BeginTransaction();foreach(Work w in batch)w.Execute(c,tx);tx.Commit();foreach(Work w in batch){if(w.Sample){Interlocked.Increment(ref committed);Interlocked.Increment(ref totalSamples);}if(w.Completion!=null)w.Completion.TrySetResult(null);}}
                    catch(Exception ex){if(tx!=null)try{tx.Rollback();}catch{}Fail(ex is IOException?ex.Message:"SQLite 写入失败："+ex.Message);foreach(Work w in batch)if(w.Completion!=null)w.Completion.TrySetException(ex);}
                    finally{if(tx!=null)tx.Dispose();}
                }
            }}catch(Exception ex){Fail("SQLite 初始化/写入失败："+ex.Message);ready.TrySetException(ex);RejectQueued(ex);}
            finally{if(!enabled)RejectQueued(new IOException(error));if(owns)try{writerMutex.ReleaseMutex();}catch{}if(writerMutex!=null)writerMutex.Dispose();}
        }
        void CheckDiskSpace(bool initial)
        {
            string root=Path.GetPathRoot(Path.GetFullPath(dbPath));DriveInfo drive=new DriveInfo(root);long free=drive.AvailableFreeSpace;Interlocked.Exchange(ref availableDiskBytes,free);Volatile.Write(ref diskWarning,free<1024L*1024*1024?1:0);if(free<100L*1024*1024)throw new IOException("数据库磁盘可用空间低于100 MiB，已停止接受新记录。");
        }
        static string MutexPathKey(string path){using(MD5 md5=MD5.Create()){byte[] hash=md5.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));return BitConverter.ToString(hash).Replace("-","");}}
        static void Init(SQLiteConnection c)
        {
            using(SQLiteCommand cmd=new SQLiteCommand("PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA cache_size=-8192; PRAGMA temp_store=FILE",c))cmd.ExecuteNonQuery();
            int version; using(SQLiteCommand cmd=new SQLiteCommand("PRAGMA user_version",c))version=Convert.ToInt32(cmd.ExecuteScalar()); if(version>4)throw new InvalidOperationException("数据库版本高于当前程序支持版本："+version);
            string[] ddl={"CREATE TABLE IF NOT EXISTS sessions(id INTEGER PRIMARY KEY AUTOINCREMENT,source TEXT NOT NULL,address INTEGER NOT NULL,started_utc_ms INTEGER NOT NULL,ended_utc_ms INTEGER,label TEXT)","CREATE TABLE IF NOT EXISTS samples(id INTEGER PRIMARY KEY AUTOINCREMENT,session_id INTEGER NOT NULL,source TEXT NOT NULL,address INTEGER NOT NULL,pack INTEGER NOT NULL,received_utc_ms INTEGER NOT NULL,voltage_cV INTEGER NOT NULL,current_cA INTEGER NOT NULL,soc INTEGER NOT NULL,soh INTEGER NOT NULL,remaining_cAh INTEGER NOT NULL,total_cAh INTEGER NOT NULL,cycles INTEGER NOT NULL,raw_frame BLOB,payload BLOB,alarm_payload BLOB)","CREATE INDEX IF NOT EXISTS ix_samples_time_pack ON samples(received_utc_ms,pack,id)","CREATE TABLE IF NOT EXISTS cell_values(sample_id INTEGER NOT NULL,cell_index INTEGER NOT NULL,millivolts INTEGER NOT NULL,PRIMARY KEY(sample_id,cell_index))","CREATE TABLE IF NOT EXISTS temperature_values(sample_id INTEGER NOT NULL,measurement_index INTEGER NOT NULL,celsius INTEGER NOT NULL,PRIMARY KEY(sample_id,measurement_index))","CREATE TABLE IF NOT EXISTS alarm_observations(id INTEGER PRIMARY KEY AUTOINCREMENT,session_id INTEGER NOT NULL,source TEXT NOT NULL,address INTEGER NOT NULL,pack INTEGER NOT NULL,received_utc_ms INTEGER NOT NULL,decoded_text TEXT,raw_frame BLOB,payload BLOB)","CREATE TABLE IF NOT EXISTS recording_policies(id INTEGER PRIMARY KEY AUTOINCREMENT,session_id INTEGER NOT NULL,changed_utc_ms INTEGER NOT NULL,period_seconds INTEGER NOT NULL)"};
            using(SQLiteTransaction tx=c.BeginTransaction()) { foreach(string sql in ddl)using(SQLiteCommand cmd=new SQLiteCommand(sql,c,tx))cmd.ExecuteNonQuery();
                if(version<2){using(SQLiteCommand m=new SQLiteCommand("ALTER TABLE samples ADD COLUMN balance_low_raw INTEGER NULL",c,tx))m.ExecuteNonQuery();using(SQLiteCommand m=new SQLiteCommand("ALTER TABLE samples ADD COLUMN balance_high_raw INTEGER NULL",c,tx))m.ExecuteNonQuery();using(SQLiteCommand m=new SQLiteCommand("ALTER TABLE samples ADD COLUMN humidity_pct INTEGER NULL",c,tx))m.ExecuteNonQuery();}
                if(version<3){using(SQLiteCommand m=new SQLiteCommand("ALTER TABLE samples ADD COLUMN acquisition_round INTEGER NULL",c,tx))m.ExecuteNonQuery();using(SQLiteCommand m=new SQLiteCommand("ALTER TABLE samples ADD COLUMN period_seconds INTEGER NULL",c,tx))m.ExecuteNonQuery();}
                if(version<4){using(SQLiteCommand m=new SQLiteCommand("ALTER TABLE alarm_observations ADD COLUMN acquisition_round INTEGER NULL",c,tx))m.ExecuteNonQuery();using(SQLiteCommand m=new SQLiteCommand("ALTER TABLE alarm_observations ADD COLUMN period_seconds INTEGER NULL",c,tx))m.ExecuteNonQuery();}
                using(SQLiteCommand versionCmd=new SQLiteCommand("PRAGMA user_version=4",c,tx))versionCmd.ExecuteNonQuery();tx.Commit(); }
        }
        void Fail(string message){lock(stateLock){enabled=false;error=message;}}
        static readonly DateTime UnixEpoch = new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc);
        static long UtcNowMs(){return ToMs(DateTime.UtcNow);} static long ToMs(DateTime d){return (long)(d.ToUniversalTime()-UnixEpoch).TotalMilliseconds;}
        static DateTime FromMs(long ms){return UnixEpoch.AddMilliseconds(ms);}
        void RejectQueued(Exception ex){Work w;while(queue.TryTake(out w))if(w.Completion!=null)w.Completion.TrySetException(ex);}
        public void Dispose(){if(stopping)return;Exception stopError=null;try{if(sessionId>0)StopSessionAsync().GetAwaiter().GetResult();}catch(Exception ex){stopError=ex;}stopping=true;if(!worker.Join(10000)){Fail("SQLite writer did not drain within 10 seconds.");throw new TimeoutException(error);}queue.Dispose();if(stopError!=null)throw new IOException("Could not close recording session cleanly",stopError);}
    }
}
