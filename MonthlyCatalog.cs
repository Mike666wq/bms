using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace BmsSerialDemo
{
    public sealed class CatalogEntry
    {
        public string Source { get; internal set; }
        public string Period { get; internal set; }
        public string Name { get; internal set; }
        public string Path { get; internal set; }
        public long Bytes { get; internal set; }
        public bool Legacy { get; internal set; }
    }
    // Read-only discovery and bounded page merge. The directory is a rebuildable catalog; SQLite remains authoritative.
    public sealed class MonthlyCatalog
    {
        readonly string dataRoot, deviceId;
        static readonly DateTime Epoch = new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc);
        public MonthlyCatalog(string root,string id){dataRoot=Path.GetFullPath(root);deviceId=id;}
        public IList<string> Discover(string source)
        {
            return Discover(source,DateTime.MinValue,DateTime.MaxValue);
        }
        public IList<CatalogEntry> Inventory()
        {
            List<CatalogEntry> entries=new List<CatalogEntry>();
            foreach(string source in new[]{"serial","simulation"})foreach(string path in Discover(source))
            {
                FileInfo info=new FileInfo(path);string name=Path.GetFileName(path);
                Match month=Regex.Match(name,"_([0-9]{4}-[0-9]{2})_[0-9]{3,}\\.db$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
                Match cycle=Regex.Match(name,"_cycle_([0-9]{8})T[0-9]{9}Z?_([0-9]{1,4})d_",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
                string period=month.Success?month.Groups[1].Value:cycle.Success?cycle.Groups[1].Value+" / "+cycle.Groups[2].Value+"天":"历史库";
                entries.Add(new CatalogEntry{Source=source,Period=period,Name=name,Path=path,Bytes=info.Exists?info.Length:0,Legacy=!month.Success&&!cycle.Success});
            }
            return entries.OrderBy(x=>x.Source,StringComparer.Ordinal).ThenBy(x=>x.Period,StringComparer.Ordinal).ToArray();
        }
        IList<string> Discover(string source,DateTime fromUtc,DateTime toUtc)
        {
            if(source!="serial"&&source!="simulation")throw new ArgumentException("无效的数据源");
            List<string> paths=new List<string>();string legacy=Path.Combine(dataRoot,source+".db");if(File.Exists(legacy))paths.Add(legacy);
            fromUtc=fromUtc.ToUniversalTime();toUtc=toUtc.ToUniversalTime();TimeZoneInfo zone=TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
            string dir=Path.Combine(dataRoot,deviceId);
            if(Directory.Exists(dir))
            {
                Regex monthly=new Regex("^"+Regex.Escape(source)+"_([0-9]{4})-([0-9]{2})_[0-9]{3,}\\.db$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
                foreach(string candidate in Directory.GetFiles(dir,source+"_*.db",SearchOption.TopDirectoryOnly))
                {
                    // C19：周期库按文件名 [起始,起始+天数) 外扩 ±2 天安全余量做时间裁剪；
                    // 余量覆盖时钟调整/时区差异，区间内实际 SQL 时间戳仍是查询准绳。
                    Match cm=Regex.Match(Path.GetFileName(candidate),"^"+Regex.Escape(source)+"_cycle_([0-9]{8}T[0-9]{9})Z?_([0-9]{1,4})d_[0-9]{6,}_[0-9]{3,}\\.db$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
                    if(cm.Success)
                    {
                        DateTime cycleLocalStart;int cycleDays;
                        if(!DateTime.TryParseExact(cm.Groups[1].Value,"yyyyMMdd'T'HHmmssfff",CultureInfo.InvariantCulture,DateTimeStyles.None,out cycleLocalStart)||!Int32.TryParse(cm.Groups[2].Value,NumberStyles.None,CultureInfo.InvariantCulture,out cycleDays)||cycleDays<1)continue;
                        DateTime cycleStart=TimeZoneInfo.ConvertTimeToUtc(cycleLocalStart,zone),cycleEnd=cycleStart.AddDays(cycleDays);
                        if(cycleStart.AddDays(-2)<toUtc&&cycleEnd.AddDays(2)>fromUtc)paths.Add(candidate);
                        continue;
                    }
                    Match m=monthly.Match(Path.GetFileName(candidate));if(!m.Success)continue;
                    int year=Int32.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture),month=Int32.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture);
                    if(year<1||year>=9999||month<1||month>12)continue;
                    DateTime localStart=new DateTime(year,month,1,0,0,0,DateTimeKind.Unspecified),localEnd=localStart.AddMonths(1);
                    DateTime start=TimeZoneInfo.ConvertTimeToUtc(localStart,zone),end=TimeZoneInfo.ConvertTimeToUtc(localEnd,zone);
                    if(start<toUtc&&end>fromUtc)paths.Add(candidate);
                }
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
        }
        public IList<StoredSample> QueryPage(string source,DateTime fromUtc,DateTime toUtc,int pack,StoredSample before,int limit)
        {
            if(limit<1||limit>500)throw new ArgumentOutOfRangeException("limit");
            long f=ToMs(fromUtc),to=ToMs(toUtc);List<StoredSample> page=new List<StoredSample>();
            foreach(string path in Discover(source,fromUtc,toUtc))
            {
                string cs="Data Source="+path+";Version=3;Read Only=True;BusyTimeout=5000;";
                string db=path.ToLowerInvariant();List<StoredSample> perDb=new List<StoredSample>();
                using(SQLiteConnection c=new SQLiteConnection(cs)){c.Open();string sourceExpr=Col(c,"samples","source","s"),sessionExpr=Col(c,"samples","session_id","s"),roundExpr=Col(c,"samples","acquisition_round","s"),periodExpr=Col(c,"samples","period_seconds","s");
                string sql="SELECT s.id,s.received_utc_ms,s.address,s.pack,s.soc,s.soh,s.cycles,s.voltage_cV,s.current_cA,s.remaining_cAh,s.total_cAh,"+sourceExpr+","+sessionExpr+","+roundExpr+","+periodExpr+" FROM samples s WHERE s.received_utc_ms>=@f AND s.received_utc_ms<@to AND (@p=0 OR s.pack=@p) AND (@has=0 OR s.received_utc_ms<@bt OR (s.received_utc_ms=@bt AND (@db<@bdb OR (@db=@bdb AND s.id<@bid)))) ORDER BY s.received_utc_ms DESC,s.id DESC LIMIT @n";
                using(SQLiteCommand q=new SQLiteCommand(sql,c))
                {q.Parameters.AddWithValue("@f",f);q.Parameters.AddWithValue("@to",to);q.Parameters.AddWithValue("@p",pack);q.Parameters.AddWithValue("@has",before==null?0:1);q.Parameters.AddWithValue("@bt",before==null?0:ToMs(before.ReceivedUtc));q.Parameters.AddWithValue("@db",db);q.Parameters.AddWithValue("@bdb",before==null?"":before.Database);q.Parameters.AddWithValue("@bid",before==null?0:before.Id);q.Parameters.AddWithValue("@n",limit);
                 using(SQLiteDataReader r=q.ExecuteReader())while(r.Read()){long sid=r.IsDBNull(12)?0:Convert.ToInt64(r.GetValue(12),CultureInfo.InvariantCulture);perDb.Add(new StoredSample{Database=db,Id=r.GetInt64(0),Source=r.IsDBNull(11)?source:r.GetString(11),HistorySessionId=HistorySession(db,sid),AcquisitionRound=r.IsDBNull(13)?0:Convert.ToInt64(r.GetValue(13),CultureInfo.InvariantCulture),PeriodSeconds=r.IsDBNull(14)?(int?)null:Convert.ToInt32(r.GetValue(14),CultureInfo.InvariantCulture),ReceivedUtc=FromMs(r.GetInt64(1)),Address=(byte)r.GetInt32(2),Pack=r.GetInt32(3),Soc=r.GetInt32(4),Soh=r.GetInt32(5),Cycles=r.GetInt32(6),Voltage=r.GetInt32(7)/100.0,Current=r.GetInt32(8)/100.0,RemainingAh=r.GetInt32(9)/100.0,TotalAh=r.GetInt32(10)/100.0,Cells=new int[0],Temperatures=new int[0]});}}}
                page.AddRange(perDb);page=page.OrderByDescending(x=>x.ReceivedUtc).ThenByDescending(x=>x.Database,StringComparer.Ordinal).ThenByDescending(x=>x.Id).Take(limit).ToList();
            }
            ReadChildren(page);return page;
        }
        public async Task<XlsxExportResult> ExportXlsxAsync(string source,DateTime fromUtc,DateTime toUtc,int pack,string parent,IProgress<XlsxExportProgress> progress,CancellationToken token)
        {
            if(toUtc<=fromUtc)throw new ArgumentException("结束时间必须晚于开始时间");ExportNaming.VerifyDestination(parent);ExportNaming.CheckDestinationSpace(parent);if(!await SampleStore.ExportGate.WaitAsync(0,token).ConfigureAwait(false))throw new InvalidOperationException("已有CSV或XLSX导出任务正在进行，请稍后重试。");
            try{return await ExportXlsxWorkerAsync(source,fromUtc,toUtc,pack,parent,progress,token).ConfigureAwait(false);}finally{SampleStore.ExportGate.Release();}
        }
        async Task<XlsxExportResult> ExportXlsxWorkerAsync(string source,DateTime fromUtc,DateTime toUtc,int pack,string parent,IProgress<XlsxExportProgress> progress,CancellationToken token)
        {
            string stem=ExportNaming.Stem(source,pack,fromUtc.ToUniversalTime(),toUtc.ToUniversalTime());string final=ExportNaming.AvailableDirectory(parent,stem);if(Path.Combine(final,stem+"_999999.xlsx").Length>245)throw new PathTooLongException("导出路径过长，请选择更短的保存目录。");
            string[] frozen=await Task.Run(delegate{return Discover(source,fromUtc,toUtc).ToArray();},token).ConfigureAwait(false);Dictionary<string,SampleStore.ExportBounds> bounds=new Dictionary<string,SampleStore.ExportBounds>(StringComparer.Ordinal);
            await Task.Run(delegate{foreach(string db in frozen){token.ThrowIfCancellationRequested();string cs="Data Source="+db+";Version=3;Read Only=True;BusyTimeout=5000;";SampleStore.ExportBounds b=new SampleStore.ExportBounds();using(SQLiteConnection c=new SQLiteConnection(cs)){c.Open();b.Samples=MaximumId(c,"samples");b.Alarms=MaximumId(c,"alarm_observations");b.Policies=MaximumId(c,"recording_policies");}b.AlarmTextParts=1;bounds[db]=b;}},token).ConfigureAwait(false);
            string temp=ExportNaming.CreateWorkDirectory();List<string> names=new List<string>();long sampleRows=0,alarmRows=0,policyRows=0;int volume=0,globalVolume=0;
            try
            {
                foreach(string db in frozen){token.ThrowIfCancellationRequested();volume++;Report(progress,"正在导出数据源分卷",volume,frozen.Length);SampleStore store=new SampleStore(source,db,false,true);try{await store.Ready.ConfigureAwait(false);string work=Path.Combine(temp,"work");Directory.CreateDirectory(work);XlsxExportResult r;try{r=await store.ExportXlsxFrozenAsync(fromUtc,toUtc,pack,work,bounds[db],progress,token).ConfigureAwait(false);}catch(InvalidOperationException empty){if(empty.Message.Contains("没有可导出的数据"))continue;throw;}sampleRows+=r.SampleRows;alarmRows+=r.AlarmRows;policyRows+=r.PolicyRows;foreach(string child in Directory.GetFiles(r.OutputDirectory,"*.xlsx")){string dest=Path.Combine(temp,stem+"_"+(++globalVolume).ToString("000",CultureInfo.InvariantCulture)+".xlsx");File.Move(child,dest);names.Add(Path.GetFileName(dest));}Directory.Delete(r.OutputDirectory,true);}
                finally{store.Dispose();}
                }
                string workDir=Path.Combine(temp,"work");if(Directory.Exists(workDir))Directory.Delete(workDir,true);if(names.Count==0)throw new InvalidOperationException("所选时间范围和 Pack 没有可导出的数据。");token.ThrowIfCancellationRequested();File.WriteAllText(Path.Combine(temp,stem+"_清单.txt"),"本次完整导出\r\n时间范围（本地，开始含结束不含）="+fromUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture)+" — "+toUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture)+"\r\n数据源="+(source=="simulation"?"模拟":"真实")+"\r\nPack="+(pack==0?"全部":pack.ToString(CultureInfo.InvariantCulture))+"\r\n参与数据库数="+frozen.Length+"\r\n"+String.Join("\r\n",names.ToArray())+"\r\n实时数据行数="+sampleRows+"\r\n告警行数="+alarmRows+"\r\n策略事件行数="+policyRows+"\r\n",new UTF8Encoding(true));ExportNaming.PublishDirectory(temp,parent,final,token);return new XlsxExportResult{OutputDirectory=final,Files=names.ToArray(),FileCount=names.Count,SampleRows=sampleRows,AlarmRows=alarmRows,PolicyRows=policyRows};
            }
            finally{ExportNaming.TryDeleteDirectory(temp);}
        }
        static void Report(IProgress<XlsxExportProgress> p,string phase,long done,long total){if(p!=null)p.Report(new XlsxExportProgress{Phase=phase,CompletedRows=done,TotalRows=total});}
        static long MaximumId(SQLiteConnection c,string table){if(!HasTable(c,table))return -1;using(SQLiteCommand q=new SQLiteCommand("SELECT COALESCE(MAX(id),0) FROM "+table,c))return Convert.ToInt64(q.ExecuteScalar(),CultureInfo.InvariantCulture);}
        sealed class CsvEvent
        {
            public long UtcMs,Id;public int Kind;public string Db,Line;
        }
        public async Task<long> ExportCsvAsync(string source,string path,DateTime fromUtc,DateTime toUtc,int pack,IProgress<XlsxExportProgress> progress,CancellationToken token)
        {
            if(toUtc<=fromUtc)throw new ArgumentException("结束时间必须晚于开始时间");ExportNaming.VerifyFileDestination(path);if(!await SampleStore.ExportGate.WaitAsync(0,token).ConfigureAwait(false))throw new InvalidOperationException("已有CSV或XLSX导出任务正在进行，请稍后重试。");
            try{return await Task.Run(delegate{return ExportCsvWorker(source,path,fromUtc.ToUniversalTime(),toUtc.ToUniversalTime(),pack,progress,token);},token).ConfigureAwait(false);}finally{SampleStore.ExportGate.Release();}
        }
        public string GetSuggestedCsvFileName(string source,DateTime fromUtc,DateTime toUtc,int pack){return ExportNaming.CsvSuggestion(source,pack,fromUtc.ToUniversalTime(),toUtc.ToUniversalTime());}
        long ExportCsvWorker(string source,string path,DateTime fromUtc,DateTime toUtc,int pack,IProgress<XlsxExportProgress> progress,CancellationToken token)
        {
            string[] files=Discover(source,fromUtc,toUtc).ToArray();Dictionary<string,long[]> upper=new Dictionary<string,long[]>(StringComparer.Ordinal);foreach(string db in files){token.ThrowIfCancellationRequested();using(SQLiteConnection c=OpenReadOnly(db)){upper[db]=new[]{MaximumId(c,"samples"),MaximumId(c,"alarm_observations"),MaximumId(c,"recording_policies")};}}
            if(String.IsNullOrWhiteSpace(path))throw new ArgumentException("请选择CSV目标路径","path");if(File.Exists(path)||Directory.Exists(path))throw new IOException("目标文件已存在，未覆盖："+path);string workDir=ExportNaming.CreateWorkDirectory();string partial=Path.Combine(workDir,Path.GetFileName(path)+".partial");long written=0,cursorTime=Int64.MinValue,cursorId=0;string cursorDb="";int cursorKind=-1;long f=ToMs(fromUtc),t=ToMs(toUtc);
            try
            {
                string root=Path.GetPathRoot(Path.GetFullPath(path));if(new DriveInfo(root).AvailableFreeSpace<100L*1024*1024)throw new IOException("CSV输出卷可用空间低于100 MiB；导出已停止。");
                using(StreamWriter output=new StreamWriter(partial,false,new UTF8Encoding(true)))
                {
                    output.WriteLine("record_type,database,id,local_time,source,address,pack,voltage_V,current_A,soc_pct,soh_pct,remaining_Ah,total_Ah,cycles,balance_low_raw,balance_high_raw,humidity_pct,acquisition_round,period_seconds,cells_mV_ordered,temperatures_C_ordered,raw_frame_hex,payload_hex,alarm_payload_hex,alarm_text,session_id");
                    while(true)
                    {
                        token.ThrowIfCancellationRequested();List<CsvEvent> best=new List<CsvEvent>(200);
                        foreach(string db in files){using(SQLiteConnection c=OpenReadOnly(db)){List<CsvEvent> local=new List<CsvEvent>();ReadCsvSamples(c,db,upper[db][0],f,t,pack,cursorTime,cursorDb,cursorKind,cursorId,local,200);ReadCsvAlarms(c,db,upper[db][1],f,t,pack,cursorTime,cursorDb,cursorKind,cursorId,local,200);ReadCsvPolicies(c,db,upper[db][2],f,t,cursorTime,cursorDb,cursorKind,cursorId,local,200);best.AddRange(local);best.Sort(CompareCsv);if(best.Count>200)best.RemoveRange(200,best.Count-200);}}
                        if(best.Count==0)break;foreach(CsvEvent e in best){token.ThrowIfCancellationRequested();output.WriteLine(e.Line);cursorTime=e.UtcMs;cursorDb=e.Db;cursorKind=e.Kind;cursorId=e.Id;written++;}if(progress!=null)progress.Report(new XlsxExportProgress{Phase="正在合并跨库CSV",CompletedRows=written,TotalRows=0});if(best.Count<200)break;
                    }
                }
                if(written==0)throw new InvalidOperationException("所选时间范围和 Pack 没有可导出的数据。");token.ThrowIfCancellationRequested();if(File.Exists(path)||Directory.Exists(path))throw new IOException("目标文件已存在，未覆盖："+path);ExportNaming.PublishFile(partial,path,token);return written;
            }
            finally{ExportNaming.TryDeleteDirectory(workDir);}
        }
        static SQLiteConnection OpenReadOnly(string path){SQLiteConnection c=new SQLiteConnection("Data Source="+path+";Version=3;Read Only=True;BusyTimeout=5000;");c.Open();return c;}
        static int CompareCsv(CsvEvent a,CsvEvent b){int n=a.UtcMs.CompareTo(b.UtcMs);if(n!=0)return n;n=StringComparer.Ordinal.Compare(a.Db,b.Db);if(n!=0)return n;n=a.Kind.CompareTo(b.Kind);return n!=0?n:a.Id.CompareTo(b.Id);}
        static string Csv(params string[] fields){for(int i=0;i<fields.Length;i++){string v=fields[i]??"";fields[i]="\""+v.Replace("\"","\"\"")+"\"";}return String.Join(",",fields);}
        static string MsLocal(long n){return FromMs(n).ToLocalTime().ToString("o",CultureInfo.InvariantCulture);}static string HexBytes(byte[] b){return BitConverter.ToString(b??new byte[0]).Replace("-","");}
        static string HistorySession(string database,long sessionId){using(SHA256 sha=SHA256.Create()){byte[] hash=sha.ComputeHash(Encoding.UTF8.GetBytes((database??"").ToLowerInvariant()+"|"+sessionId.ToString(CultureInfo.InvariantCulture)));return "bms-history-"+BitConverter.ToString(hash,0,16).Replace("-","").ToLowerInvariant();}}
        static bool HasColumn(SQLiteConnection c,string table,string column){using(SQLiteCommand q=new SQLiteCommand("PRAGMA table_info("+table+")",c))using(SQLiteDataReader r=q.ExecuteReader())while(r.Read())if(String.Equals(r.GetString(1),column,StringComparison.OrdinalIgnoreCase))return true;return false;}
        static bool HasTable(SQLiteConnection c,string table){using(SQLiteCommand q=new SQLiteCommand("SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n",c)){q.Parameters.AddWithValue("@n",table);return q.ExecuteScalar()!=null;}}
        static string Col(SQLiteConnection c,string table,string column,string alias=null){return HasColumn(c,table,column)?(alias==null?column:alias+"."+column):"NULL";}
        static void ReadCsvSamples(SQLiteConnection c,string db,long upper,long f,long t,int pack,long ct,string cdb,int ck,long cid,List<CsvEvent> rows,int limit)
        {
            if(!HasTable(c,"samples"))return;string[] cols={"address","pack","soc","soh","voltage_cV","current_cA","remaining_cAh","total_cAh","cycles","balance_low_raw","balance_high_raw","humidity_pct","acquisition_round","period_seconds","raw_frame","payload","alarm_payload","session_id"};StringBuilder select=new StringBuilder("SELECT s.id,s.received_utc_ms,s.source");foreach(string col in cols)select.Append(',').Append(Col(c,"samples",col,"s"));string cellExpr=HasTable(c,"cell_values")?"COALESCE((SELECT group_concat(millivolts,';') FROM cell_values WHERE sample_id=s.id ORDER BY cell_index),'')":"''",tempExpr=HasTable(c,"temperature_values")?"COALESCE((SELECT group_concat(celsius,';') FROM temperature_values WHERE sample_id=s.id ORDER BY measurement_index),'')":"''";select.Append(",").Append(cellExpr).Append(" AS cells,").Append(tempExpr).Append(" AS temps FROM samples s WHERE s.id<=@u AND s.received_utc_ms>=@f AND s.received_utc_ms<@t AND (@p=0 OR s.pack=@p) AND (@has=0 OR s.received_utc_ms>@ct OR (s.received_utc_ms=@ct AND (@db>@cdb OR (@db=@cdb AND (0>@ck OR (0=@ck AND s.id>@cid)))))) ORDER BY s.received_utc_ms,s.id LIMIT @n");
            using(SQLiteCommand q=new SQLiteCommand(select.ToString(),c)){BindEventQuery(q,db,upper,f,t,pack,ct,cdb,ck,cid,limit,0);using(SQLiteDataReader r=q.ExecuteReader())while(r.Read()){long id=r.GetInt64(0),ms=r.GetInt64(1);string[] raw=new string[18];for(int i=0;i<18;i++)raw[i]=r.IsDBNull(3+i)?"":Convert.ToString(r[3+i],CultureInfo.InvariantCulture);int v=Convert.ToInt32(raw[4],CultureInfo.InvariantCulture),cur=Convert.ToInt32(raw[5],CultureInfo.InvariantCulture),rem=Convert.ToInt32(raw[6],CultureInfo.InvariantCulture),tot=Convert.ToInt32(raw[7],CultureInfo.InvariantCulture);rows.Add(new CsvEvent{UtcMs=ms,Db=db.ToLowerInvariant(),Kind=0,Id=id,Line=Csv("sample",db.ToLowerInvariant(),id.ToString(CultureInfo.InvariantCulture),MsLocal(ms),r.IsDBNull(2)?"":r.GetString(2),raw[0],raw[1],(v/100.0).ToString("0.00",CultureInfo.InvariantCulture),(cur/100.0).ToString("0.00",CultureInfo.InvariantCulture),raw[2],raw[3],(rem/100.0).ToString("0.00",CultureInfo.InvariantCulture),(tot/100.0).ToString("0.00",CultureInfo.InvariantCulture),raw[8],raw[9],raw[10],raw[11],raw[12],raw[13],r.IsDBNull(21)?"":r.GetString(21),r.IsDBNull(22)?"":r.GetString(22),HexBytes(r.IsDBNull(17)?new byte[0]:(byte[])r[17]),HexBytes(r.IsDBNull(18)?new byte[0]:(byte[])r[18]),HexBytes(r.IsDBNull(19)?new byte[0]:(byte[])r[19]),"",raw[17])});}}
        }
        static void ReadCsvAlarms(SQLiteConnection c,string db,long upper,long f,long t,int pack,long ct,string cdb,int ck,long cid,List<CsvEvent> rows,int limit)
        {
            if(!HasTable(c,"alarm_observations"))return;string sql="SELECT id,received_utc_ms,source,address,pack,decoded_text,raw_frame,payload,"+Col(c,"alarm_observations","acquisition_round")+","+Col(c,"alarm_observations","period_seconds")+","+Col(c,"alarm_observations","session_id")+" FROM alarm_observations WHERE id<=@u AND received_utc_ms>=@f AND received_utc_ms<@t AND (@p=0 OR pack=@p) AND (@has=0 OR received_utc_ms>@ct OR (received_utc_ms=@ct AND (@db>@cdb OR (@db=@cdb AND (1>@ck OR (1=@ck AND id>@cid)))))) ORDER BY received_utc_ms,id LIMIT @n";
            using(SQLiteCommand q=new SQLiteCommand(sql,c)){BindEventQuery(q,db,upper,f,t,pack,ct,cdb,ck,cid,limit,1);using(SQLiteDataReader r=q.ExecuteReader())while(r.Read()){long id=r.GetInt64(0),ms=r.GetInt64(1);string text=r.IsDBNull(5)?"":r.GetString(5);rows.Add(new CsvEvent{UtcMs=ms,Db=db.ToLowerInvariant(),Kind=1,Id=id,Line=Csv("alarm",db.ToLowerInvariant(),id.ToString(CultureInfo.InvariantCulture),MsLocal(ms),r.IsDBNull(2)?"":r.GetString(2),r.IsDBNull(3)?"":Convert.ToString(r[3],CultureInfo.InvariantCulture),r.IsDBNull(4)?"":Convert.ToString(r[4],CultureInfo.InvariantCulture),"","","","","","","","","","",r.IsDBNull(8)?"":Convert.ToString(r[8],CultureInfo.InvariantCulture),r.IsDBNull(9)?"":Convert.ToString(r[9],CultureInfo.InvariantCulture),"","",HexBytes(r.IsDBNull(6)?new byte[0]:(byte[])r[6]),HexBytes(r.IsDBNull(7)?new byte[0]:(byte[])r[7]),"",text,r.IsDBNull(10)?"":Convert.ToString(r[10],CultureInfo.InvariantCulture))});}}
        }
        static void ReadCsvPolicies(SQLiteConnection c,string db,long upper,long f,long t,long ct,string cdb,int ck,long cid,List<CsvEvent> rows,int limit)
        {
            if(!HasTable(c,"recording_policies"))return;string sql="SELECT p.id,p.changed_utc_ms,p.period_seconds,p.session_id,"+(HasTable(c,"sessions")?"s.source":"NULL")+","+(HasTable(c,"sessions")?"s.address":"NULL")+" FROM recording_policies p"+(HasTable(c,"sessions")?" LEFT JOIN sessions s ON s.id=p.session_id":"")+" WHERE p.id<=@u AND p.changed_utc_ms>=@f AND p.changed_utc_ms<@t AND (@has=0 OR p.changed_utc_ms>@ct OR (p.changed_utc_ms=@ct AND (@db>@cdb OR (@db=@cdb AND (2>@ck OR (2=@ck AND p.id>@cid)))))) ORDER BY p.changed_utc_ms,p.id LIMIT @n";
            using(SQLiteCommand q=new SQLiteCommand(sql,c)){BindEventQuery(q,db,upper,f,t,0,ct,cdb,ck,cid,limit,2);using(SQLiteDataReader r=q.ExecuteReader())while(r.Read()){long id=r.GetInt64(0),ms=r.GetInt64(1);rows.Add(new CsvEvent{UtcMs=ms,Db=db.ToLowerInvariant(),Kind=2,Id=id,Line=Csv("policy",db.ToLowerInvariant(),id.ToString(CultureInfo.InvariantCulture),MsLocal(ms),r.IsDBNull(4)?"":r.GetString(4),r.IsDBNull(5)?"":Convert.ToString(r[5],CultureInfo.InvariantCulture),"","","","","","","","","","","",r.IsDBNull(2)?"":Convert.ToString(r[2],CultureInfo.InvariantCulture),"","","","","","",r.IsDBNull(3)?"":Convert.ToString(r[3],CultureInfo.InvariantCulture))});}}
        }
        static void BindEventQuery(SQLiteCommand q,string db,long upper,long f,long t,int pack,long ct,string cdb,int ck,long cid,int limit,int kind){q.Parameters.AddWithValue("@u",upper);q.Parameters.AddWithValue("@f",f);q.Parameters.AddWithValue("@t",t);q.Parameters.AddWithValue("@p",pack);q.Parameters.AddWithValue("@has",ct==Int64.MinValue?0:1);q.Parameters.AddWithValue("@ct",ct);q.Parameters.AddWithValue("@db",db.ToLowerInvariant());q.Parameters.AddWithValue("@cdb",cdb??"");q.Parameters.AddWithValue("@ck",ck);q.Parameters.AddWithValue("@cid",cid);q.Parameters.AddWithValue("@n",limit);}
        static void ReadChildren(List<StoredSample> rows)
        {
            foreach(IGrouping<string,StoredSample> group in rows.GroupBy(x=>x.Database,StringComparer.Ordinal))
            {
                List<StoredSample> items=group.ToList();if(items.Count==0)continue;Dictionary<long,StoredSample> map=items.ToDictionary(x=>x.Id);Dictionary<long,List<int>> cells=new Dictionary<long,List<int>>(),temps=new Dictionary<long,List<int>>();foreach(StoredSample s in items){cells[s.Id]=new List<int>();temps[s.Id]=new List<int>();}
                string ids=String.Join(",",items.Select(x=>x.Id.ToString(CultureInfo.InvariantCulture)).ToArray());string cs="Data Source="+group.Key+";Version=3;Read Only=True;BusyTimeout=5000;";
                using(SQLiteConnection c=new SQLiteConnection(cs)){c.Open();if(HasTable(c,"cell_values"))using(SQLiteCommand q=new SQLiteCommand("SELECT sample_id,millivolts FROM cell_values WHERE sample_id IN ("+ids+") ORDER BY sample_id,cell_index",c))using(SQLiteDataReader r=q.ExecuteReader())while(r.Read())cells[r.GetInt64(0)].Add(r.GetInt32(1));if(HasTable(c,"temperature_values"))using(SQLiteCommand q=new SQLiteCommand("SELECT sample_id,celsius FROM temperature_values WHERE sample_id IN ("+ids+") ORDER BY sample_id,measurement_index",c))using(SQLiteDataReader r=q.ExecuteReader())while(r.Read())temps[r.GetInt64(0)].Add(r.GetInt32(1));}
                foreach(StoredSample s in items){s.Cells=cells[s.Id].ToArray();s.Temperatures=temps[s.Id].ToArray();}
            }
        }
        internal static int RunSelfTests()
        {
            string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".catalog-test"),id="d";if(Directory.Exists(root))Directory.Delete(root,true);string dir=Path.Combine(root,id);Directory.CreateDirectory(dir);
            try
            {
                DateTime utc=new DateTime(2026,10,1,0,0,0,DateTimeKind.Utc);string legacy=Path.Combine(root,"serial.db"),sep=Path.Combine(dir,"serial_2026-09_001.db"),oct=Path.Combine(dir,"serial_2026-10_001.db"),old=Path.Combine(dir,"serial_2025-01_001.db");CreateTestDb(legacy,utc,1,3001);CreateTestDb(sep,utc.AddHours(-9),1,3002);CreateTestDb(oct,utc.AddMinutes(1),2,3003);CreateLegacyDb(old,new DateTime(2025,1,15,0,0,0,DateTimeKind.Utc));CreateTestDb(Path.Combine(dir,"serial_notes.db"),utc,8,3999);
                MonthlyCatalog catalog=new MonthlyCatalog(root,id);IList<string> found=catalog.Discover("serial");if(found.Count!=4)throw new Exception("monthly catalog pattern or legacy discovery failed: "+found.Count);
                DateTime f=new DateTime(2025,1,1,0,0,0,DateTimeKind.Utc),t=utc.AddHours(1);List<StoredSample> all=new List<StoredSample>();StoredSample cursor=null;while(true){IList<StoredSample> page=catalog.QueryPage("serial",f,t,0,cursor,1);if(page.Count==0)break;if(page.Count!=1)throw new Exception("catalog page size contract failed");all.Add(page[0]);cursor=page[0];if(all.Count>5)throw new Exception("catalog cursor repeated rows");}
                if(all.Count!=4||all.Select(x=>x.Id).Distinct().Count()!=2||all.Select(x=>x.Database).Distinct(StringComparer.Ordinal).Count()!=4)throw new Exception("composite cursor missed duplicate IDs across databases (rows="+all.Count+", ids="+String.Join(",",all.Select(x=>x.Id))+", dbs="+String.Join("|",all.Select(x=>x.Database))+")");
                StoredSample latest=all[0];if(latest.Id!=2||latest.Cells.Length!=1||latest.Cells[0]!=3003)throw new Exception("catalog UTC ordering or child fields failed");
                IList<string> narrow=catalog.Discover("serial",utc.AddMinutes(1),utc.AddMinutes(2));if(narrow.Count!=2||narrow.Any(x=>x.Contains("2026-09")||x.Contains("2025-01")))throw new Exception("month-range catalog pruning included an unrelated volume");
                string farCycle=Path.Combine(dir,"serial_cycle_20200101T000000000_7d_000001_001.db");CreateLegacyDb(farCycle,new DateTime(2020,1,2,0,0,0,DateTimeKind.Utc));
                IList<string> cyclePruned=catalog.Discover("serial",f,t);if(cyclePruned.Any(x=>x.IndexOf("_cycle_",StringComparison.OrdinalIgnoreCase)>=0))throw new Exception("C19 cycle catalog pruning included an out-of-range volume");
                IList<string> cycleFull=catalog.Discover("serial");if(!cycleFull.Any(x=>x.IndexOf("_cycle_",StringComparison.OrdinalIgnoreCase)>=0))throw new Exception("C19 cycle volume lost in full-range discovery");
                string noSamples=Path.Combine(dir,"serial_cycle_20260901T000000000_30d_000002_001.db");CreateTableLessDb(noSamples);
                XlsxExportResult tolerant=catalog.ExportXlsxAsync("serial",f,t,0,Path.Combine(root,"xlsx-tolerant"),null,CancellationToken.None).GetAwaiter().GetResult();if(tolerant.SampleRows!=4||tolerant.AlarmRows!=3)throw new Exception("C21 missing-table volume broke cross-library export");
                string csv=Path.Combine(root,"merged.csv");long csvRows=catalog.ExportCsvAsync("serial",csv,f,t,0,null,CancellationToken.None).GetAwaiter().GetResult();if(csvRows!=10)throw new Exception("cross-library CSV row count failed: "+csvRows);string[] csvLines=File.ReadAllLines(csv,Encoding.UTF8);if(csvLines.Length!=11||!csvLines[1].Contains("serial_2025-01")||!csvLines.Any(x=>x.Contains("0102"))||!csvLines.Any(x=>x.Contains("serial.db"))||!csvLines.Any(x=>x.Contains("alarm fixture"))||!csvLines.Any(x=>x.Contains("policy")))throw new Exception("cross-library CSV content/order/fields/old schema failed");
                XlsxExportResult xlsx=catalog.ExportXlsxAsync("serial",f,t,0,root,null,CancellationToken.None).GetAwaiter().GetResult();if(xlsx.SampleRows!=4||xlsx.AlarmRows!=3||xlsx.PolicyRows!=3||xlsx.FileCount<4)throw new Exception("cross-library XLSX row count/volumes failed");bool sawCell=false,sawVoltage=false;foreach(string name in xlsx.Files){using(ZipArchive archive=ZipFile.OpenRead(Path.Combine(xlsx.OutputDirectory,name))){ZipArchiveEntry main=archive.GetEntry("xl/worksheets/sheet1.xml");using(StreamReader reader=new StreamReader(main.Open())){string xml=reader.ReadToEnd();sawCell|=xml.Contains("3001")||xml.Contains("3002")||xml.Contains("3003");sawVoltage|=xml.Contains("<v>52.00</v>");}}}if(!sawCell||!sawVoltage)throw new Exception("XLSX full-field sample data missing");
                string simulation=Path.Combine(dir,"simulation_2025-01_001.db");CreatePagingDb(simulation,new DateTime(2025,1,15,0,0,0,DateTimeKind.Utc),205);MonthlyCatalog catalogs=new MonthlyCatalog(root,id);string frozenCsv=Path.Combine(root,"frozen.csv");bool inserted=false;InlineProgress<XlsxExportProgress> freezeProgress=new InlineProgress<XlsxExportProgress>(delegate(XlsxExportProgress p){if(!inserted&&p.CompletedRows>=200){inserted=true;InsertPagingSample(simulation,206,new DateTime(2025,1,15,0,0,0,DateTimeKind.Utc));}});long frozenRows=catalogs.ExportCsvAsync("simulation",frozenCsv,f,t,0,freezeProgress,CancellationToken.None).GetAwaiter().GetResult();string csvContent=File.ReadAllText(frozenCsv);IList<StoredSample> simPage=catalogs.QueryPage("simulation",f,t,0,null,10);if(frozenRows!=205||csvContent.Contains("\"206\""))throw new Exception("CSV did not freeze the per-library maximum ID (rows="+frozenRows+", inserted="+inserted+", discovered="+catalogs.Discover("simulation",f,t).Count+", page="+simPage.Count+", range="+f.ToString("o")+"/"+t.ToString("o")+", path="+simulation+")");
                string canceledCsv=Path.Combine(root,"canceled.csv");CancellationTokenSource csvCancel=new CancellationTokenSource();try{catalogs.ExportCsvAsync("simulation",canceledCsv,f,t,0,new InlineProgress<XlsxExportProgress>(delegate(XlsxExportProgress p){if(p.CompletedRows>0)csvCancel.Cancel();}),csvCancel.Token).GetAwaiter().GetResult();throw new Exception("CSV cancellation was ignored");}catch(OperationCanceledException){}if(File.Exists(canceledCsv)||Directory.GetFiles(root,"canceled.csv.partial-*",SearchOption.TopDirectoryOnly).Length!=0)throw new Exception("CSV cancellation left a partial result");
                string xlsxParent=Path.Combine(root,"xlsx");Directory.CreateDirectory(xlsxParent);bool xlsxInserted=false;XlsxExportResult frozenXlsx=catalogs.ExportXlsxAsync("simulation",f,t,0,xlsxParent,new InlineProgress<XlsxExportProgress>(delegate(XlsxExportProgress p){if(!xlsxInserted&&p.Phase.StartsWith("正在导出",StringComparison.Ordinal)){xlsxInserted=true;InsertPagingSample(simulation,207,new DateTime(2025,1,15,0,0,0,DateTimeKind.Utc));}}),CancellationToken.None).GetAwaiter().GetResult();if(frozenXlsx.SampleRows!=206)throw new Exception("XLSX did not freeze the per-library maximum ID");
                string cancelParent=Path.Combine(root,"xlsx-cancel");Directory.CreateDirectory(cancelParent);CancellationTokenSource xlsxCancel=new CancellationTokenSource();try{catalogs.ExportXlsxAsync("simulation",f,t,0,cancelParent,new InlineProgress<XlsxExportProgress>(delegate(XlsxExportProgress p){if(p.Phase.StartsWith("正在导出",StringComparison.Ordinal))xlsxCancel.Cancel();}),xlsxCancel.Token).GetAwaiter().GetResult();throw new Exception("XLSX cancellation was ignored");}catch(OperationCanceledException){}if(Directory.GetDirectories(cancelParent,"*",SearchOption.TopDirectoryOnly).Length!=0||Directory.GetFiles(cancelParent,"*",SearchOption.TopDirectoryOnly).Length!=0)throw new Exception("XLSX cancellation left a partial directory");
                return 13;
            }
            finally{string checkedRoot=Path.GetFullPath(root),baseRoot=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);if(checkedRoot.StartsWith(baseRoot,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(checkedRoot))Directory.Delete(checkedRoot,true);}
        }
        static void CreateTestDb(string path,DateTime utc,long id,int cell)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));using(SQLiteConnection c=new SQLiteConnection("Data Source="+path+";Version=3;")){c.Open();string ddl="CREATE TABLE sessions(id INTEGER PRIMARY KEY,source TEXT,address INTEGER);CREATE TABLE samples(id INTEGER PRIMARY KEY,session_id INTEGER,source TEXT,address INTEGER,pack INTEGER,received_utc_ms INTEGER,soc INTEGER,soh INTEGER,cycles INTEGER,voltage_cV INTEGER,current_cA INTEGER,remaining_cAh INTEGER,total_cAh INTEGER,balance_low_raw INTEGER,balance_high_raw INTEGER,humidity_pct INTEGER,raw_frame BLOB,payload BLOB,alarm_payload BLOB,acquisition_round INTEGER,period_seconds INTEGER);CREATE TABLE cell_values(sample_id INTEGER,cell_index INTEGER,millivolts INTEGER);CREATE TABLE temperature_values(sample_id INTEGER,measurement_index INTEGER,celsius INTEGER);CREATE TABLE alarm_observations(id INTEGER PRIMARY KEY,session_id INTEGER,source TEXT,address INTEGER,pack INTEGER,received_utc_ms INTEGER,decoded_text TEXT,raw_frame BLOB,payload BLOB,acquisition_round INTEGER,period_seconds INTEGER);CREATE TABLE recording_policies(id INTEGER PRIMARY KEY,session_id INTEGER,changed_utc_ms INTEGER,period_seconds INTEGER);";using(SQLiteCommand q=new SQLiteCommand(ddl,c))q.ExecuteNonQuery();long ms=(long)(utc-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds;using(SQLiteCommand q=new SQLiteCommand("INSERT INTO sessions VALUES(1,'serial',1);INSERT INTO samples(id,session_id,source,address,pack,received_utc_ms,soc,soh,cycles,voltage_cV,current_cA,remaining_cAh,total_cAh,balance_low_raw,balance_high_raw,humidity_pct,raw_frame,payload,alarm_payload,acquisition_round,period_seconds) VALUES(@id,1,'serial',1,1,@t,80,99,10,5200,-123,1000,2000,3,4,55,X'0102',X'0304',X'0506',42,5);INSERT INTO cell_values VALUES(@id,1,@v);INSERT INTO temperature_values VALUES(@id,1,25);INSERT INTO alarm_observations VALUES(1,1,'serial',1,1,@t,'alarm fixture',X'0708',X'090A',42,5);INSERT INTO recording_policies VALUES(1,1,@t,5)",c)){q.Parameters.AddWithValue("@id",id);q.Parameters.AddWithValue("@t",ms);q.Parameters.AddWithValue("@v",cell);q.ExecuteNonQuery();}}
        }
        static void CreateLegacyDb(string path,DateTime utc){Directory.CreateDirectory(Path.GetDirectoryName(path));using(SQLiteConnection c=new SQLiteConnection("Data Source="+path+";Version=3;")){c.Open();using(SQLiteCommand q=new SQLiteCommand("CREATE TABLE samples(id INTEGER PRIMARY KEY,received_utc_ms INTEGER,source TEXT,address INTEGER,pack INTEGER,soc INTEGER,soh INTEGER,cycles INTEGER,voltage_cV INTEGER,current_cA INTEGER,remaining_cAh INTEGER,total_cAh INTEGER)",c))q.ExecuteNonQuery();long ms=(long)(utc-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds;using(SQLiteCommand q=new SQLiteCommand("INSERT INTO samples VALUES(1,@t,'serial',1,1,80,99,10,5200,-123,1000,2000)",c)){q.Parameters.AddWithValue("@t",ms);q.ExecuteNonQuery();}}}
        static void CreateTableLessDb(string path){Directory.CreateDirectory(Path.GetDirectoryName(path));using(SQLiteConnection c=new SQLiteConnection("Data Source="+path+";Version=3;")){c.Open();using(SQLiteCommand q=new SQLiteCommand("CREATE TABLE notes(id INTEGER)",c))q.ExecuteNonQuery();}}
        static void CreatePagingDb(string path,DateTime utc,int count){Directory.CreateDirectory(Path.GetDirectoryName(path));using(SQLiteConnection c=new SQLiteConnection("Data Source="+path+";Version=3;")){c.Open();using(SQLiteCommand q=new SQLiteCommand("CREATE TABLE samples(id INTEGER PRIMARY KEY,received_utc_ms INTEGER,source TEXT,address INTEGER,pack INTEGER,soc INTEGER,soh INTEGER,cycles INTEGER,voltage_cV INTEGER,current_cA INTEGER,remaining_cAh INTEGER,total_cAh INTEGER)",c))q.ExecuteNonQuery();using(SQLiteTransaction tx=c.BeginTransaction()){for(int i=1;i<=count;i++)using(SQLiteCommand q=new SQLiteCommand("INSERT INTO samples VALUES(@id,@t,'simulation',1,1,80,99,10,5200,-123,1000,2000)",c,tx)){q.Parameters.AddWithValue("@id",i);q.Parameters.AddWithValue("@t",(long)(utc-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds);q.ExecuteNonQuery();}tx.Commit();} }}
        static void InsertPagingSample(string path,long id,DateTime utc){using(SQLiteConnection c=new SQLiteConnection("Data Source="+path+";Version=3;")){c.Open();using(SQLiteCommand q=new SQLiteCommand("INSERT INTO samples VALUES(@id,@t,'simulation',1,1,80,99,10,5200,-123,1000,2000)",c)){q.Parameters.AddWithValue("@id",id);q.Parameters.AddWithValue("@t",(long)(utc-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds);q.ExecuteNonQuery();}}}
        sealed class InlineProgress<T>:IProgress<T>{readonly Action<T> report;public InlineProgress(Action<T> callback){report=callback;}public void Report(T value){report(value);}}
        static long ToMs(DateTime d){return (long)(d.ToUniversalTime()-Epoch).TotalMilliseconds;}static DateTime FromMs(long n){return Epoch.AddMilliseconds(n);}
    }
}
