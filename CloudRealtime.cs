using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Acme.Cloud;
namespace BmsSerialDemo
{
    [DataContract]
    public sealed class CloudHeartbeatReply
    {
        [DataMember(Name="subscriptionId",IsRequired=true)]public string SubscriptionId{get;set;}
        [DataMember(Name="leaseSeconds",IsRequired=true)]public int LeaseSeconds{get;set;}
        [DataMember(Name="requestedPacks",IsRequired=true)]public int[] RequestedPacks{get;set;}
        [DataMember(Name="backfillSeconds",EmitDefaultValue=false)]public int BackfillSeconds{get;set;}
        // 以下为设备配置下行（云端说了算）。全部可选且不带 IsRequired：
        // 旧云端回包里没有这些字段，标了必填就会反序列化失败、整端离线。
        [DataMember(Name="protocolVersion",EmitDefaultValue=false)]public int? ProtocolVersion{get;set;}
        [DataMember(Name="state",EmitDefaultValue=false)]public string State{get;set;}
        [DataMember(Name="uploadAllowed",EmitDefaultValue=false)]public bool? UploadAllowed{get;set;}
        [DataMember(Name="configVersion",EmitDefaultValue=false)]public long? ConfigVersion{get;set;}
        [DataMember(Name="config",EmitDefaultValue=false)]public CloudDeviceConfig Config{get;set;}
        [DataMember(Name="commands",EmitDefaultValue=false)]public CloudDeviceCommand[] Commands{get;set;}
    }
    [DataContract]
    sealed class CloudHeartbeatRequest
    {
        [DataMember(Name="deviceId")]public string DeviceId{get;set;}
        [DataMember(Name="alias")]public string Alias{get;set;}
        [DataMember(Name="module",EmitDefaultValue=false)]public string Module{get;set;}
        [DataMember(Name="schemaVersion",EmitDefaultValue=false)]public int SchemaVersion{get;set;}
        // 设备自述：把「只有现场能看到的体检结果」搭车送到云端。全部可选，坏数据由服务端降级。
        [DataMember(Name="client",EmitDefaultValue=false)]public CloudClientReport Client{get;set;}
        // 接入协议字段必须留在类末尾：契约检查器只读本类开头的 600 字符窗口，
        // 把 Module/SchemaVersion 挤出窗口会被判定为「心跳缺少模块声明」。
        // protocolVersion 单独一个整数，绝不复用 schemaVersion（实验端有 schemaVersion===1 的硬断言）。
        [DataMember(Name="protocolVersion",EmitDefaultValue=false)]public int? ProtocolVersion{get;set;}
        [DataMember(Name="machineKey",EmitDefaultValue=false)]public string MachineKey{get;set;}
        [DataMember(Name="reportedConfigVersion",EmitDefaultValue=false)]public long? ReportedConfigVersion{get;set;}
        [DataMember(Name="reportedCommandId",EmitDefaultValue=false)]public string ReportedCommandId{get;set;}
    }
    [DataContract]
    public sealed class CloudSnapshotPost
    {
        [DataMember(Name="subscriptionId")]public string SubscriptionId{get;set;}
        [DataMember(Name="snapshot")]public CloudSnapshotEnvelope Snapshot{get;set;}
    }
    [DataContract]
    public sealed class CloudBackfillPoint
    {
        [DataMember(Name="source")]public string Source{get;set;}
        [DataMember(Name="address")]public byte Address{get;set;}
        [DataMember(Name="pack")]public int Pack{get;set;}
        [DataMember(Name="connectionSessionId")]public string ConnectionSessionId{get;set;}
        [DataMember(Name="sequence")]public long Sequence{get;set;}
        [DataMember(Name="capturedUtc")]public string CapturedUtc{get;set;}
        [DataMember(Name="periodSeconds",EmitDefaultValue=false)]public int? PeriodSeconds{get;set;}
        [DataMember(Name="voltageCentivolts")]public int VoltageCentivolts{get;set;}
        [DataMember(Name="currentCentiamps")]public int CurrentCentiamps{get;set;}
        [DataMember(Name="socPercent")]public int SocPercent{get;set;}
    }
    [DataContract]
    public sealed class CloudBackfillPost
    {
        [DataMember(Name="subscriptionId")]public string SubscriptionId{get;set;}
        [DataMember(Name="schemaVersion")]public int SchemaVersion{get;set;}
        [DataMember(Name="module")]public string Module{get;set;}
        [DataMember(Name="points")]public CloudBackfillPoint[] Points{get;set;}
    }
    public interface ICloudTransport
    {
        Task<CloudHeartbeatReply> HeartbeatAsync(CloudConfiguration configuration,CancellationToken token);
        Task SendSnapshotAsync(CloudConfiguration configuration,CloudSnapshotPost post,CancellationToken token);
        Task SendBackfillAsync(CloudConfiguration configuration,CloudBackfillPost post,CancellationToken token);
    }
    /// <summary>
    /// 设备自助接入（认领/轮询/确认）。刻意独立成一个接口：
    /// 自测里的 FakeCloudTransport 不必实现它，老路径与既有断言一行都不用改。
    /// </summary>
    public interface ICloudEnrollmentTransport
    {
        Task<CloudClaimReply> ClaimAsync(CloudConfiguration configuration,CloudClaimRequest request,CancellationToken token);
        Task<CloudClaimReply> ClaimStatusAsync(CloudConfiguration configuration,string claimId,CancellationToken token);
        Task<CloudAckReply> AcknowledgeAsync(CloudConfiguration configuration,CloudAckRequest request,CancellationToken token);
    }
    public sealed class HttpsCloudTransport:ICloudTransport,ICloudTokenRefresher,ICloudEnrollmentTransport
    {
        // 传输引擎在 Acme.Cloud：请求拼装、5s 整体时限、64KiB 响应上限、禁重定向、
        // 超时/取消/HTTP 状态码分类，三端共用同一份实现。
        static readonly DataContractJsonSerializer replySerializer=new DataContractJsonSerializer(typeof(CloudHeartbeatReply));
        static readonly DataContractJsonSerializer acknowledgementSerializer=new DataContractJsonSerializer(typeof(CloudAcknowledgement));
        static readonly DataContractJsonSerializer refreshSerializer=new DataContractJsonSerializer(typeof(CloudTokenRefreshReply));
        static readonly DataContractJsonSerializer claimSerializer=new DataContractJsonSerializer(typeof(CloudClaimReply));
        static readonly DataContractJsonSerializer ackSerializer=new DataContractJsonSerializer(typeof(CloudAckReply));
        readonly CloudHttpTransport engine;
        public HttpsCloudTransport(){engine=new CloudHttpTransport();}
        internal HttpsCloudTransport(Func<Uri,HttpWebRequest> factory,int timeoutMilliseconds,bool testLoopback){engine=new CloudHttpTransport(factory,timeoutMilliseconds,testLoopback);}
        // 服务层把自述块的生产者挂在这里；传输层只负责搭车，不关心内容。
        public Func<CloudClientReport> ClientReportProvider;
        // 接入状态的生产者：心跳要带上「我已应用到的配置版本」和机器钥匙。
        public Func<CloudEnrollment> EnrollmentProvider;
        public Task<CloudHeartbeatReply> HeartbeatAsync(CloudConfiguration configuration,CancellationToken token)
        {
            CloudHeartbeatRequest request=new CloudHeartbeatRequest{DeviceId=configuration.DeviceId,Alias=configuration.Alias,Module="bms",SchemaVersion=1};
            CloudEnrollment enrollment=EnrollmentProvider!=null?EnrollmentProvider():null;
            if(enrollment!=null)
            {
                // protocolVersion 只在真接入时才声明：没认领过的设备仍旧发老格式，
                // 让云端继续按 v1 处理，回包与升级前逐字节一致。
                // 协议能力是「这份代码支持什么」，不是「本地状态文件记了什么」。
                // 依赖状态文件会导致：状态文件丢了 → 退化发旧协议 → 云端停用/删除都下发不到。
                request.ProtocolVersion=2;
                if(!String.IsNullOrEmpty(enrollment.MachineKey))request.MachineKey=enrollment.MachineKey;
                if(enrollment.ReportedConfigVersion>0)request.ReportedConfigVersion=enrollment.ReportedConfigVersion;
                if(!String.IsNullOrEmpty(enrollment.LastAppliedCommandId))request.ReportedCommandId=enrollment.LastAppliedCommandId;
            }
            Func<CloudClientReport> provider=ClientReportProvider;
            if(provider!=null)try{request.Client=provider();}catch{}
            return engine.SendAsync<CloudHeartbeatReply>(configuration,"/api/realtime/heartbeat",request,replySerializer,token);
        }
        /// <summary>用当前令牌换新令牌：设备自助续期，管理员不参与。</summary>
        public Task<CloudTokenRefreshReply> RefreshTokenAsync(CloudConfiguration configuration,CancellationToken token)
        {return engine.SendAsync<CloudTokenRefreshReply>(configuration,"/api/realtime/device/refresh",new CloudRefreshRequest(),refreshSerializer,token);}
        public async Task SendSnapshotAsync(CloudConfiguration configuration,CloudSnapshotPost post,CancellationToken token)
        {CloudAcknowledgement a=await engine.SendAsync<CloudAcknowledgement>(configuration,"/api/realtime/snapshots",post,acknowledgementSerializer,token).ConfigureAwait(false);if(a==null||!a.Accepted)throw new InvalidDataException("云端未确认快照");}
        public async Task SendBackfillAsync(CloudConfiguration configuration,CloudBackfillPost post,CancellationToken token)
        {CloudAcknowledgement a=await engine.SendAsync<CloudAcknowledgement>(configuration,"/api/realtime/backfill",post,acknowledgementSerializer,token).ConfigureAwait(false);if(a==null||!a.Accepted)throw new InvalidDataException("云端未确认历史回填");}
        // 认领三连：传入的 configuration 只是「地址 + Bearer」的临时载体，绝不 Save()。
        public Task<CloudClaimReply> ClaimAsync(CloudConfiguration configuration,CloudClaimRequest request,CancellationToken token)
        {return engine.SendAsync<CloudClaimReply>(configuration,CloudDeviceRoutes.Claim,request,claimSerializer,token);}
        public Task<CloudClaimReply> ClaimStatusAsync(CloudConfiguration configuration,string claimId,CancellationToken token)
        {return engine.SendAsync<CloudClaimReply>(configuration,CloudDeviceRoutes.ClaimStatus(claimId),new CloudRefreshRequest(),claimSerializer,token);}
        public Task<CloudAckReply> AcknowledgeAsync(CloudConfiguration configuration,CloudAckRequest request,CancellationToken token)
        {return engine.SendAsync<CloudAckReply>(configuration,CloudDeviceRoutes.Ack,request,ackSerializer,token);}
    }
    public sealed class CloudRealtimeStatus
    {
        public string State{get;internal set;}public string Detail{get;internal set;}public DateTime? LastContactUtc{get;internal set;}public DateTime? LastUploadUtc{get;internal set;}public DateTime? LastSampleUtc{get;internal set;}public bool HasSubscription{get;internal set;}public long Sent{get;internal set;}public long Failed{get;internal set;}public long Merged{get;internal set;}
    }
    public sealed class CloudRealtimeService: IRealtimePublisher,IDisposable
    {
        sealed class Pending{public RealtimeSnapshot Snapshot;public long Sequence;public long UpdatedTimestamp;public double CapturedAgeSeconds;}
        readonly object sync=new object(),backfillGate=new object();readonly Func<CloudConfiguration> configuration;readonly ICloudTransport transport;readonly Func<string,DateTime,DateTime,int,IList<StoredSample>> historyReader;readonly CancellationTokenSource shutdown=new CancellationTokenSource();readonly Dictionary<string,Pending> latest=new Dictionary<string,Pending>(StringComparer.Ordinal);readonly Dictionary<string,long> sent=new Dictionary<string,long>(StringComparer.Ordinal);string sessionId=Guid.NewGuid().ToString("N"),backfilledSubscription=null,backfillRunningSubscription=null;long sessionGeneration;readonly Task worker;Task backfillTask=Task.FromResult(0);CancellationTokenSource backfillCancel;readonly Random jitter=new Random();CloudRealtimeStatus status=new CloudRealtimeStatus{State="未启用",Detail="云端连接默认关闭"};long sequence,merged,notifiedSent,notifiedFailed;bool disposed;DateTime? notifiedContact,notifiedUpload;
        // 离线诊断：把本机看到的失败阶段与次数攒起来，随下一次心跳送出去。
        readonly DateTime startedUtc=DateTime.UtcNow;int consecutiveFailures;DateTime? lastSuccessUtc,lastRefreshUtc;string lastErrorStage,lastErrorCode,lastErrorAt;int lastErrorHttp,lastErrorCount;
        // 设备接入：云端是真相源，这份只是本地缓存 + 认领期凭据。
        // settingsDirectory 为 null 时整个接入能力关闭（自测与旧调用点因此一行都不用改）。
        CloudEnrollment enrollment;readonly string enrollmentPath;readonly object enrollmentGate=new object();
        // null = 云端还没说过这一项，按「允许」处理；收到配置后才有确定值。
        bool? uploadAllowed,allowSimulation;string deviceState;
        public event EventHandler StatusChanged;
        public CloudRealtimeStatus Status{get{lock(sync)return Copy(status);}}
        // 给只读界面用的接入视图：只暴露字符串，不把可变对象交出去。
        public string EnrollmentState{get{CloudEnrollment e;lock(enrollmentGate)e=enrollment;if(e==null)return "未启用接入";switch(e.State){case "active":return "已接入";case "pending":return "等待管理员批准";case "approved":return "已批准，正在激活";case "suspended":return "已被云端停用（本地上传暂停）";case "revoked":return "已被云端注销";case "rejected":return "接入被拒绝";default:return "未接入";}}}
        public string EnrollmentAlias{get{CloudEnrollment e;lock(enrollmentGate)e=enrollment;return e==null||String.IsNullOrEmpty(e.Alias)?"（等待云端分配）":e.Alias;}}
        public string EnrollmentAccess{get{CloudEnrollment e;lock(enrollmentGate)e=enrollment;if(e==null||e.ProtocolVersion<=0)return "（等待云端下发）";List<string> parts=new List<string>();if(e.AllowedPacks!=null&&e.AllowedPacks.Length>0)parts.Add("Pack "+String.Join("、",Array.ConvertAll(e.AllowedPacks,delegate(int v){return v.ToString();})));if(e.AllowedAddresses!=null&&e.AllowedAddresses.Length>0)parts.Add("地址 "+String.Join("、",Array.ConvertAll(e.AllowedAddresses,delegate(int v){return v.ToString();})));parts.Add(e.AllowSimulation?"允许模拟数据":"仅真实设备");return String.Join(" · ",parts.ToArray());}}
        public string MachineKeyMasked{get{CloudEnrollment e;lock(enrollmentGate)e=enrollment;if(e==null||String.IsNullOrEmpty(e.MachineKey))return "—";string k=e.MachineKey;return k.Length<=10?k:k.Substring(0,7)+"…"+k.Substring(k.Length-4);}}
        public CloudRealtimeService(Func<CloudConfiguration> getConfiguration,ICloudTransport cloudTransport=null,Func<string,DateTime,DateTime,int,IList<StoredSample>> readHistory=null,string settingsDirectory=null)
        {
            configuration=getConfiguration;transport=cloudTransport??new HttpsCloudTransport();historyReader=readHistory;
            HttpsCloudTransport https=transport as HttpsCloudTransport;
            if(https!=null)https.ClientReportProvider=BuildClientReport;
            if(!String.IsNullOrEmpty(settingsDirectory))
            {
                // 机器钥匙本地自生成、人不用看：它是「我是这台机器」的唯一凭据，
                // 与 device-id.txt 完全解耦（那是业务编号，绝不由这里生成或改写）。
                try
                {
                    enrollmentPath=Path.Combine(settingsDirectory,CloudEnrollmentStore.FileName);
                    enrollment=CloudEnrollmentStore.Load(enrollmentPath,CloudIdentity.LoadOrCreateMachineKey("BmsSerialDemo",AppDomain.CurrentDomain.BaseDirectory));
                    uploadAllowed=enrollment.IsActive?enrollment.UploadAllowed:(bool?)null;
                    allowSimulation=enrollment.ProtocolVersion>0?enrollment.AllowSimulation:(bool?)null;
                    deviceState=enrollment.State;
                }
                catch(Exception e){CloudNetworkErrors.Write(e);}
            }
            if(https!=null)https.EnrollmentProvider=delegate{return enrollment;};
            worker=Task.Run((Func<Task>)RunAsync);
        }
        public void Publish(RealtimeSnapshot snapshot)
        {
            if(snapshot==null||snapshot.Pack<1||snapshot.Pack>16)return;lock(sync){if(disposed)return;CloudConfiguration c;try{c=configuration();}catch{return;}// 上传闸门只在这里拦：采集与 SQLite 在 Program.cs 是另一条语句，物理上碰不到。
                // uploadAllowed/allowSimulation 为 null 表示云端还没下发过，按放行处理（旧行为不变）。
                if(c==null||!c.Enabled||uploadAllowed==false||(snapshot.Source=="simulation"&&!(allowSimulation??c.IncludeSimulation)))return;string key=snapshot.Source+":"+snapshot.Address+":"+snapshot.Pack;Pending old;if(latest.TryGetValue(key,out old))merged++;else if(latest.Count>=16){string drop=null;long oldest=Int64.MaxValue;foreach(KeyValuePair<string,Pending> pair in latest)if(pair.Value.UpdatedTimestamp<oldest){oldest=pair.Value.UpdatedTimestamp;drop=pair.Key;}if(drop!=null){latest.Remove(drop);sent.Remove(drop);merged++;}}DateTime now=DateTime.UtcNow;latest[key]=new Pending{Snapshot=snapshot,Sequence=++sequence,UpdatedTimestamp=Stopwatch.GetTimestamp(),CapturedAgeSeconds=Math.Max(0,(now-snapshot.ReceivedUtc.ToUniversalTime()).TotalSeconds)};status.Merged=merged;} }
        public void ResetCaptureSession(){lock(sync){if(disposed)return;latest.Clear();sent.Clear();sequence=0;status.LastSampleUtc=null;status.LastUploadUtc=null;sessionId=Guid.NewGuid().ToString("N");sessionGeneration++;}}
        /// <summary>清掉本地认领状态，让下一拍重新走认领流程。人依旧不需要填任何东西。</summary>
        public void ResetEnrollment()
        {
            lock(enrollmentGate)
            {
                if(enrollment==null)return;
                enrollment.ClaimSecret=null;enrollment.ClaimId=null;enrollment.State="unclaimed";enrollment.ProtocolVersion=0;
                try{CloudEnrollmentStore.Save(enrollment);}catch(Exception e){CloudNetworkErrors.Write(e);}
            }
            lock(sync){uploadAllowed=null;allowSimulation=null;deviceState="unclaimed";}
            lastEnrollTick=0;
        }
        static string HistoricalSession(StoredSample row){string value=String.IsNullOrWhiteSpace(row.HistorySessionId)?"bms-history-"+Path.GetFileNameWithoutExtension(row.Database??"unknown"):row.HistorySessionId;return value.Length<=128?value:value.Substring(0,128);}
        void CancelBackfill()
        {
            CancellationTokenSource cancel;lock(backfillGate){cancel=backfillCancel;backfillRunningSubscription=null;}
            if(cancel!=null)try{cancel.Cancel();}catch(ObjectDisposedException){}
        }
        void ScheduleWarmBackfill(CloudConfiguration c,CloudHeartbeatReply reply,HashSet<int> requested,string subscription)
        {
            if(historyReader==null||reply==null||reply.BackfillSeconds<=0||String.IsNullOrEmpty(subscription))return;
            CancellationTokenSource previous=null,next=null;HashSet<int> requestedCopy=new HashSet<int>(requested);
            lock(backfillGate)
            {
                if(String.Equals(backfilledSubscription,subscription,StringComparison.Ordinal))return;
                if(String.Equals(backfillRunningSubscription,subscription,StringComparison.Ordinal)&&backfillTask!=null&&!backfillTask.IsCompleted)return;
                previous=backfillCancel;next=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);backfillCancel=next;backfillRunningSubscription=subscription;
                backfillTask=Task.Run(async delegate
                {
                    try{await Task.Delay(250,next.Token).ConfigureAwait(false);await WarmBackfillAsync(c,reply,requestedCopy,subscription,next.Token).ConfigureAwait(false);}
                    catch(OperationCanceledException){if(shutdown.IsCancellationRequested)return;}
                    finally{lock(backfillGate){if(Object.ReferenceEquals(backfillCancel,next)){backfillCancel=null;backfillRunningSubscription=null;}}next.Dispose();}
                });
            }
            if(previous!=null)try{previous.Cancel();}catch(ObjectDisposedException){}
        }
        async Task WarmBackfillAsync(CloudConfiguration c,CloudHeartbeatReply reply,HashSet<int> requested,string subscription,CancellationToken token)
        {
            try
            {
                int seconds=Math.Min(300,reply.BackfillSeconds);DateTime toUtc=DateTime.UtcNow.AddSeconds(1),fromUtc=DateTime.UtcNow.AddSeconds(-seconds);List<StoredSample> rows=new List<StoredSample>();
                foreach(string source in c.IncludeSimulation?new[]{"serial","simulation"}:new[]{"serial"})foreach(int pack in requested){token.ThrowIfCancellationRequested();IList<StoredSample> found=historyReader(source,fromUtc,toUtc,pack);if(found!=null)rows.AddRange(found);}
                List<CloudBackfillPoint> points=rows.OrderBy(x=>x.ReceivedUtc).ThenBy(x=>x.Database,StringComparer.Ordinal).ThenBy(x=>x.Id).Select(x=>new CloudBackfillPoint{Source=String.IsNullOrEmpty(x.Source)?"serial":x.Source,Address=x.Address,Pack=x.Pack,ConnectionSessionId=HistoricalSession(x),Sequence=Math.Max(1,x.Id),CapturedUtc=x.ReceivedUtc.ToUniversalTime().ToString("o"),PeriodSeconds=x.PeriodSeconds,VoltageCentivolts=x.VoltageCentivolts,CurrentCentiamps=x.CurrentCentiamps,SocPercent=x.Soc}).ToList();
                for(int offset=0;offset<points.Count;offset+=128){token.ThrowIfCancellationRequested();CloudBackfillPoint[] batch=points.Skip(offset).Take(128).ToArray();await transport.SendBackfillAsync(c,new CloudBackfillPost{SubscriptionId=subscription,SchemaVersion=1,Module="bms",Points=batch},token).ConfigureAwait(false);if(offset+128<points.Count)await Task.Delay(50,token).ConfigureAwait(false);}
                lock(backfillGate){if(!token.IsCancellationRequested&&String.Equals(backfillRunningSubscription,subscription,StringComparison.Ordinal))backfilledSubscription=subscription;}
            }
            catch(OperationCanceledException){throw;}
            catch(Exception e){CloudNetworkErrors.Write(e);Update("在线等待观看","5分钟历史预热失败，实时上传继续",true);}
        }
        // ---------- 设备自助接入（认领 → 批准 → 一次性取令牌 → 确认） ----------
        // 节奏控制：认领/轮询不走心跳的定时器，自己算下一次该什么时候动。
        long lastEnrollTick;int enrollIntervalMs=5000;

        static string NewClaimSecret()
        {
            byte[] buffer=new byte[32];
            using(RandomNumberGenerator rng=RandomNumberGenerator.Create())rng.GetBytes(buffer);
            return Convert.ToBase64String(buffer).Replace('+','-').Replace('/','_').TrimEnd('=');
        }

        /// <summary>认领请求只要「地址 + Bearer」两个信息，用内存副本承载，绝不写盘。</summary>
        static CloudConfiguration ClaimCarrier(CloudConfiguration c,string secret)
        {
            return new CloudConfiguration{DeviceId=c.DeviceId,Alias=c.Alias,Endpoint=c.Endpoint,Enabled=true,IncludeSimulation=c.IncludeSimulation,Token=secret,SavePath=c.SavePath};
        }

        /// <summary>把云端下发的配置与状态应用到运行时。protocolVersion 缺失 = 旧云端，一律不动。</summary>
        void ApplyDeviceReply(CloudHeartbeatReply reply,CloudConfiguration c)
        {
            if(reply==null||!reply.ProtocolVersion.HasValue)return;
            bool changed=false;
            if(!String.IsNullOrEmpty(reply.State)&&!String.Equals(deviceState,reply.State,StringComparison.Ordinal)){deviceState=reply.State;changed=true;}
            if(reply.UploadAllowed.HasValue&&uploadAllowed!=reply.UploadAllowed){uploadAllowed=reply.UploadAllowed;changed=true;}
            CloudEnrollment state;lock(enrollmentGate){state=enrollment;}
            if(state!=null)
            {
                if(reply.Config!=null)
                {
                    state.ApplyConfig(reply.Config,reply.ConfigVersion.HasValue?reply.ConfigVersion.Value:0);
                    allowSimulation=state.AllowSimulation;
                    uploadAllowed=state.UploadAllowed;
                    changed=true;
                }
                if(!String.IsNullOrEmpty(reply.State)&&!String.Equals(state.State,reply.State,StringComparison.Ordinal)){state.State=reply.State;changed=true;}
                ApplyCommands(reply.Commands,state,c);
                if(changed)try{lock(enrollmentGate)CloudEnrollmentStore.Save(state);}catch(Exception e){CloudNetworkErrors.Write(e);}
            }
        }

        /// <summary>命令按 commandId 去重，只认比上次更新的那条。</summary>
        void ApplyCommands(CloudDeviceCommand[] commands,CloudEnrollment state,CloudConfiguration c)
        {
            if(commands==null||commands.Length==0)return;
            foreach(CloudDeviceCommand command in commands)
            {
                if(command==null||String.IsNullOrEmpty(command.CommandId))continue;
                if(String.Equals(command.CommandId,state.LastAppliedCommandId,StringComparison.Ordinal))continue;
                string type=command.Type??"";
                if(type=="wipe-credentials"||type=="reclaim")
                {
                    // 云端已注销：清掉令牌与认领凭据，保留机器钥匙，停止一切云网络直到人工重新认领。
                    state.ClaimSecret=null;state.ClaimId=null;state.State="revoked";state.ProtocolVersion=0;
                    state.LastAppliedCommandId=command.CommandId;
                    uploadAllowed=false;deviceState="revoked";
                    // 同时清掉设备令牌：否则客户端会一直拿着旧令牌心跳，
                    // 永远停在「已注销」而不会重新发起认领。
                    if(c!=null&&!String.IsNullOrEmpty(c.Token)){try{c.Token="";c.Save();}catch{}}
                }
                else if(type=="stop-upload"){uploadAllowed=false;state.LastAppliedCommandId=command.CommandId;}
                else if(type=="resume-upload"){uploadAllowed=true;state.LastAppliedCommandId=command.CommandId;}
                else {state.LastAppliedCommandId=command.CommandId;}
            }
        }

        /// <summary>
        /// 没有设备令牌时走这里：首次发起认领、之后轮询；批准后拿到令牌就落盘并确认。
        /// 返回 true 表示已经可以正常发心跳。
        /// </summary>
        async Task<bool> EnsureEnrolledAsync(CloudConfiguration c)
        {
            ICloudEnrollmentTransport enrollmentTransport=transport as ICloudEnrollmentTransport;
            CloudEnrollment state;lock(enrollmentGate){state=enrollment;}
            if(enrollmentTransport==null||state==null)return false;
            // 已注销也允许重新发起认领：令牌已被清空，说明确实是人工重新接入。
            long now=Stopwatch.GetTimestamp();
            if(lastEnrollTick!=0&&(now-lastEnrollTick)*1000.0/Stopwatch.Frequency<enrollIntervalMs)return false;
            lastEnrollTick=now;
            try
            {
                CloudClaimReply reply;
                if(String.IsNullOrEmpty(state.ClaimSecret)||String.IsNullOrEmpty(state.ClaimId))
                {
                    if(String.IsNullOrEmpty(state.MachineKey))state.MachineKey=CloudIdentity.LoadOrCreateMachineKey("BmsSerialDemo",AppDomain.CurrentDomain.BaseDirectory);
                    state.ClaimSecret=NewClaimSecret();
                    state.Module="bms";
                    reply=await enrollmentTransport.ClaimAsync(ClaimCarrier(c,state.ClaimSecret),
                        new CloudClaimRequest{MachineKey=state.MachineKey,Module="bms",ProtocolVersion=2,DesiredAlias=Environment.MachineName,Client=BuildClientReport()},shutdown.Token).ConfigureAwait(false);
                }
                else
                {
                    reply=await enrollmentTransport.ClaimStatusAsync(ClaimCarrier(c,state.ClaimSecret),state.ClaimId,shutdown.Token).ConfigureAwait(false);
                }
                if(reply==null){Update("等待批准","云端未返回接入结果",false);return false;}
                if(!String.IsNullOrEmpty(reply.ClaimId))state.ClaimId=reply.ClaimId;
                if(!String.IsNullOrEmpty(reply.State))state.State=reply.State;
                if(!String.IsNullOrEmpty(reply.DeviceToken)&&!String.IsNullOrEmpty(reply.DeviceId))
                {
                    // 先落盘成功，再切换运行态：中途掉电不会留下「有令牌但云端不认」的状态。
                    string previousToken=c.Token,previousId=c.DeviceId;
                    c.Token=reply.DeviceToken;c.DeviceId=reply.DeviceId;
                    try{c.Save();}catch{c.Token=previousToken;c.DeviceId=previousId;throw;}
                    state.DeviceId=reply.DeviceId;state.State="active";state.ProtocolVersion=2;
                    if(reply.Config!=null)state.ApplyConfig(reply.Config,reply.ConfigVersion.HasValue?reply.ConfigVersion.Value:0);
                    state.ClaimSecret=null;
                    try{lock(enrollmentGate)CloudEnrollmentStore.Save(state);}catch(Exception e){CloudNetworkErrors.Write(e);}
                    allowSimulation=state.AllowSimulation;uploadAllowed=state.UploadAllowed;deviceState="active";
                    try{await enrollmentTransport.AcknowledgeAsync(c,new CloudAckRequest{ClaimId=state.ClaimId,AckType="activation",DeviceId=reply.DeviceId,ConfigVersion=reply.ConfigVersion,Persisted=true},shutdown.Token).ConfigureAwait(false);}
                    catch(Exception ackError){CloudNetworkErrors.Write(ackError);}
                    enrollIntervalMs=15000;
                    Update("在线等待观看","设备已接入，等待观看租约",false);
                    return true;
                }
                if(String.Equals(reply.State,"rejected",StringComparison.Ordinal))
                {
                    state.State="rejected";try{lock(enrollmentGate)CloudEnrollmentStore.Save(state);}catch{}
                    Update("接入被拒绝",String.IsNullOrEmpty(reply.Message)?"管理员拒绝了该设备":reply.Message,false);
                    await Delay(30000);return false;
                }
                // 认领凭据必须落盘：否则进程重启会生成新 secret，而云端那条记录的哈希对不上，
                // 结果就是每次启动都 401、永远批准不了。
                try{lock(enrollmentGate)CloudEnrollmentStore.Save(state);}catch(Exception saveError){CloudNetworkErrors.Write(saveError);}
                Update("等待批准","已提交接入申请，等待管理员在云端网页批准",false);
                return false;
            }
            catch(CloudHttpException e)
            {
                CloudNetworkErrors.Write(e);
                if(e.StatusCode==429)
                {
                    // 限流不是错误，是「来太勤了」：把认领间隔翻倍，别继续撞墙。
                    enrollIntervalMs=Math.Min(60000,Math.Max(5000,enrollIntervalMs*2));
                    Update("等待批准","云端限流，已自动放慢申请节奏",false);
                    return false;
                }
                if(e.StatusCode==401||e.StatusCode==403)
                {
                    // 认领凭据失效（过期或被清空）：换一把新 secret 重新发起，不能一直撞同一个。
                    state.ClaimSecret=null;state.ClaimId=null;lastEnrollTick=0;
                    Update("重新申请接入","认领凭据已失效，正在重新申请",false);
                }
                else Update("等待批准",CloudNetworkErrors.Describe(e),false);
                return false;
            }
            catch(OperationCanceledException){throw;}
            catch(Exception e){CloudNetworkErrors.Write(e);Update("等待批准",CloudNetworkErrors.Describe(e)+"；本地采集与记录继续工作",false);return false;}
        }

        async Task RunAsync()
        {
            Stopwatch scheduler=Stopwatch.StartNew();long nextHeartbeatAt=0,nextUploadAttemptAt=0;int backoff=1;bool uploadRequest=false,uploadHasFailed=false;Stopwatch lease=Stopwatch.StartNew();long leaseDeadline=0;string subscription=null;HashSet<int> requested=new HashSet<int>();CloudConfiguration activeConfiguration=null;bool pendingRefresh=false;
            while(!shutdown.IsCancellationRequested)
            {
                CloudConfiguration c=null;try{c=configuration();}catch{}if(c==null||!c.Enabled){CancelBackfill();lock(backfillGate)backfilledSubscription=null;subscription=null;leaseDeadline=0;activeConfiguration=null;lock(sync){latest.Clear();sent.Clear();}Update("未启用","云端连接默认关闭",false);await Delay(1000);continue;}if(!ReferenceEquals(activeConfiguration,c)){CancelBackfill();lock(backfillGate)backfilledSubscription=null;activeConfiguration=c;subscription=null;leaseDeadline=0;nextHeartbeatAt=0;nextUploadAttemptAt=0;requested.Clear();}if(!c.HasValidEndpoint){Update("配置无效","云端地址无效",false);await Delay(1000);continue;}
                if(String.IsNullOrWhiteSpace(c.Token))
                {
                    // 没令牌不等于「配置错了」：新版走自助接入，人不需要填任何东西。
                    if(enrollment==null){Update("等待配置","请输入 HTTPS 服务地址和设备令牌",false);await Delay(1000);continue;}
                    if(!await EnsureEnrolledAsync(c).ConfigureAwait(false)){await Delay(1000);continue;}
                }
                try
                {
                    if(scheduler.ElapsedMilliseconds>=nextHeartbeatAt){uploadRequest=false;CloudHeartbeatReply reply=await transport.HeartbeatAsync(c,shutdown.Token).ConfigureAwait(false);if(reply==null||reply.LeaseSeconds<0||reply.LeaseSeconds>3600||reply.BackfillSeconds<0||reply.BackfillSeconds>300||reply.RequestedPacks==null||reply.RequestedPacks.Length>16||(reply.LeaseSeconds>0&&String.IsNullOrWhiteSpace(reply.SubscriptionId)))throw new InvalidDataException("心跳租约响应字段无效");int[] packs=reply.RequestedPacks;foreach(int pack in packs)if(pack<1||pack>16)throw new InvalidDataException("心跳租约包含无效 Pack");lock(sync){status.LastContactUtc=DateTime.UtcNow;}RecordSuccess();ApplyDeviceReply(reply,c);nextHeartbeatAt=scheduler.ElapsedMilliseconds+(reply.LeaseSeconds>0?Math.Min(15000,Math.Max(3000,reply.LeaseSeconds*500)):15000);if(!uploadHasFailed)backoff=1;string previousSubscription=subscription;subscription=reply.SubscriptionId;if(!String.Equals(previousSubscription,subscription,StringComparison.Ordinal)){lock(sync)sent.Clear();nextUploadAttemptAt=0;}requested=new HashSet<int>(packs);if(!String.IsNullOrEmpty(subscription)&&reply.LeaseSeconds>0){lease.Restart();leaseDeadline=(long)reply.LeaseSeconds*1000;Update("在线等待观看","收到观看租约",true);ScheduleWarmBackfill(c,reply,requested,subscription);}else{CancelBackfill();leaseDeadline=0;Update("在线等待观看","暂无观看订阅，仅发送心跳",false);}}
                    if(leaseDeadline>0&&lease.ElapsedMilliseconds<leaseDeadline&&subscription!=null){KeyValuePair<string,Pending>? candidate=null;string uploadSession=null;long uploadGeneration=0;if(scheduler.ElapsedMilliseconds>=nextUploadAttemptAt)lock(sync){CloudConfiguration current;try{current=configuration();}catch{current=null;}bool configurationCurrent=current!=null&&current.Enabled&&current.HasValidEndpoint&&!String.IsNullOrWhiteSpace(current.Token)&&ReferenceEquals(current,c);if(configurationCurrent){long now=Stopwatch.GetTimestamp();long oldest=Int64.MaxValue;foreach(KeyValuePair<string,Pending> pair in latest){Pending p=pair.Value;int period=p.Snapshot.PeriodSeconds.HasValue?p.Snapshot.PeriodSeconds.Value:0;double elapsed=(now-p.UpdatedTimestamp)/(double)Stopwatch.Frequency;if(!IsFresh(period,p.CapturedAgeSeconds,elapsed)||!requested.Contains(p.Snapshot.Pack)||(p.Snapshot.Source=="simulation"&&!(allowSimulation??current.IncludeSimulation)))continue;long sentSeq;if((!sent.TryGetValue(pair.Key,out sentSeq)||sentSeq!=p.Sequence)&&p.UpdatedTimestamp<oldest){oldest=p.UpdatedTimestamp;candidate=pair;}}}if(candidate.HasValue){uploadSession=sessionId;uploadGeneration=sessionGeneration;}}if(candidate.HasValue){uploadRequest=true;Pending p=candidate.Value.Value;CloudSnapshotEnvelope envelope=CloudSnapshotEnvelope.From(p.Snapshot,c.DeviceId,uploadSession,p.Sequence);await transport.SendSnapshotAsync(c,new CloudSnapshotPost{SubscriptionId=subscription,Snapshot=envelope},shutdown.Token).ConfigureAwait(false);uploadRequest=false;uploadHasFailed=false;bool currentSession=false;lock(sync){Pending current;if(sessionGeneration==uploadGeneration&&sessionId==uploadSession&&latest.TryGetValue(candidate.Value.Key,out current)&&current.Sequence==p.Sequence){sent[candidate.Value.Key]=p.Sequence;status.Sent++;status.LastUploadUtc=DateTime.UtcNow;status.LastSampleUtc=p.Snapshot.ReceivedUtc;currentSession=true;}}if(currentSession)Update("上传中","最近快照已确认",true);backoff=1;nextUploadAttemptAt=0;continue;}Update("在线等待观看",scheduler.ElapsedMilliseconds<nextUploadAttemptAt?"上传失败，等待重试":"租约有效；没有可上传的新鲜快照",true);}else if(leaseDeadline>0){leaseDeadline=0;Update("在线等待观看","观看租约已过期，上传已暂停",false);}
                }
                catch(OperationCanceledException){if(shutdown.IsCancellationRequested)break;lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;Update("离线重试","云端请求已取消；本地记录继续工作",false);long wait=1000L*backoff;if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait;else nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait;backoff=Math.Min(60,backoff*2);}
                catch(CloudHttpException e){CloudNetworkErrors.Write(e);RecordFailure(e);lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;if(e.StatusCode==401||e.StatusCode==403)
                {
                    CancelBackfill();leaseDeadline=0;subscription=null;
                    // 只有「云端认为我还活着」时才去续期。被注销/未激活时续期端点同样会 401，
                    // 无脑重试就是死循环 —— 这里改为清掉令牌回到认领流程，等管理员重新批准。
                    bool activeEnough=enrollment!=null&&String.Equals(deviceState,"active",StringComparison.Ordinal);
                    if(activeEnough){pendingRefresh=true;Update("认证失败","设备令牌被拒绝，正在尝试自动续期",false);}
                    else if(enrollment!=null)
                    {
                        CloudEnrollment state;lock(enrollmentGate){state=enrollment;}
                        if(state!=null&&!state.IsRevoked){state.ClaimSecret=null;state.ClaimId=null;lastEnrollTick=0;try{lock(enrollmentGate)CloudEnrollmentStore.Save(state);}catch{}}
                        Update("接入已失效","云端不再认可该设备，正在重新申请接入",false);
                    }
                    else Update("认证失败","设备令牌被拒绝",false);
                    nextHeartbeatAt=scheduler.ElapsedMilliseconds+60000;nextUploadAttemptAt=nextHeartbeatAt;
                }else if(e.StatusCode==429){Update("限流等待","云端限流，稍后重试",subscription!=null);nextHeartbeatAt=scheduler.ElapsedMilliseconds+30000;nextUploadAttemptAt=nextHeartbeatAt;}else{Update("离线重试",CloudNetworkErrors.Describe(e)+"；本地记录继续工作",false);int wait=backoff+jitter.Next(0,Math.Max(1,backoff));if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait*1000L;else{nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait*1000L;nextUploadAttemptAt=nextHeartbeatAt;}backoff=Math.Min(60,backoff*2);}}
                catch(TimeoutException e){CloudNetworkErrors.Write(e);RecordFailure(e);lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;Update("离线重试",CloudNetworkErrors.Describe(e)+"；本地记录继续工作",false);long wait=(backoff+jitter.Next(0,Math.Max(1,backoff)))*1000L;if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait;else{nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait;nextUploadAttemptAt=nextHeartbeatAt;}backoff=Math.Min(60,backoff*2);}
                catch(WebException e){CloudNetworkErrors.Write(e);RecordFailure(e);lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;Update("离线重试",CloudNetworkErrors.Describe(e)+"；本地记录继续工作",false);int wait=backoff+jitter.Next(0,Math.Max(1,backoff));if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait*1000L;else{nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait*1000L;nextUploadAttemptAt=nextHeartbeatAt;}backoff=Math.Min(60,backoff*2);}
                catch(Exception e){CloudNetworkErrors.Write(e);RecordFailure(e);lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;string description=CloudNetworkErrors.Describe(e);Update(description.StartsWith("协议错误",StringComparison.Ordinal)?"协议错误":"离线重试",description+"；本地记录继续工作",false);long wait=Math.Min(60,backoff+jitter.Next(0,Math.Max(1,backoff)));if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait*1000L;else{nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait*1000L;nextUploadAttemptAt=nextHeartbeatAt;}backoff=Math.Min(60,backoff*2);}
                if(pendingRefresh)
                {
                    pendingRefresh=false;
                    ICloudTokenRefresher refresher=transport as ICloudTokenRefresher;
                    bool tooSoon=lastRefreshUtc.HasValue&&(DateTime.UtcNow-lastRefreshUtc.Value).TotalMinutes<5;
                    if(refresher!=null&&!tooSoon)
                    {
                        try
                        {
                            CloudTokenRefreshReply renewed=await refresher.RefreshTokenAsync(c,shutdown.Token).ConfigureAwait(false);
                            if(renewed!=null&&!String.IsNullOrWhiteSpace(renewed.DeviceToken))
                            {
                                // 先落盘成功再切换运行态：换证途中掉电不会留下「有令牌但云端不认」的状态。
                                string previous=c.Token;c.Token=renewed.DeviceToken;
                                try{c.Save();}catch{c.Token=previous;throw;}
                                lastRefreshUtc=DateTime.UtcNow;
                                Update("在线等待观看","设备令牌已自动续期，重新连接中",false);
                                nextHeartbeatAt=scheduler.ElapsedMilliseconds;nextUploadAttemptAt=0;backoff=1;
                            }
                        }
                        catch(Exception refreshError){CloudNetworkErrors.Write(refreshError);Update("认证失败","自动续期失败："+CloudNetworkErrors.Describe(refreshError),false);}
                    }
                }
                await Delay(250);
            }
        }
        async Task Delay(int ms){try{await Task.Delay(ms,shutdown.Token).ConfigureAwait(false);}catch(OperationCanceledException){}}
        internal static bool IsFresh(int periodSeconds,double capturedAgeSeconds,double pendingElapsedSeconds){double maxAge=Math.Max(30,2.0*Math.Max(0,periodSeconds)+5);return capturedAgeSeconds+pendingElapsedSeconds<=maxAge;}
        void RecordSuccess(){lock(sync){consecutiveFailures=0;lastSuccessUtc=DateTime.UtcNow;lastErrorStage=null;lastErrorCode=null;lastErrorHttp=0;lastErrorAt=null;lastErrorCount=0;}}
        void RecordFailure(Exception e)
        {
            string stage=null;try{stage=CloudNetworkErrors.StageOf(e);}catch{}
            CloudHttpException http=e as CloudHttpException;
            lock(sync){consecutiveFailures++;if(stage==null)return;if(!String.Equals(stage,lastErrorStage,StringComparison.Ordinal)){lastErrorStage=stage;lastErrorAt=DateTime.UtcNow.ToString("o");lastErrorCount=0;}lastErrorCode=e.GetType().Name;lastErrorHttp=http!=null?http.StatusCode:0;lastErrorCount++;}
        }
        CloudClientReport BuildClientReport()
        {
            CloudConfiguration c=null;try{c=configuration();}catch{}
            CloudClientReport r;
            lock(sync)
            {
                r=new CloudClientReport{Version=typeof(CloudRealtimeService).Assembly.GetName().Version.ToString(),StartedUtc=startedUtc.ToString("o"),UptimeSec=(long)Math.Max(0,(DateTime.UtcNow-startedUtc).TotalSeconds),LocalTimeUtc=DateTime.UtcNow.ToString("o"),TzOffsetMin=(int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,CloudEnabled=c!=null&&c.Enabled,TokenDecryptable=c!=null?!c.SavedTokenUnreadable:true,LastSuccessUtc=lastSuccessUtc.HasValue?lastSuccessUtc.Value.ToString("o"):null,ConsecutiveFailures=consecutiveFailures};
                if(lastErrorStage!=null)r.LastError=new CloudClientError{Stage=lastErrorStage,Code=lastErrorCode,HttpStatus=lastErrorHttp,AtUtc=lastErrorAt,Count=lastErrorCount};
            }
            r.Source=new CloudClientSource{Session=sessionId};
            return r;
        }
        void Update(string state,string detail,bool subscribed){bool changed;lock(sync){changed=status.State!=state||status.Detail!=detail||status.HasSubscription!=subscribed||status.Merged!=merged||notifiedSent!=status.Sent||notifiedFailed!=status.Failed||notifiedContact!=status.LastContactUtc||notifiedUpload!=status.LastUploadUtc;status.State=state;status.Detail=detail;status.HasSubscription=subscribed;status.Merged=merged;notifiedSent=status.Sent;notifiedFailed=status.Failed;notifiedContact=status.LastContactUtc;notifiedUpload=status.LastUploadUtc;}if(changed){EventHandler h=StatusChanged;if(h!=null)try{h(this,EventArgs.Empty);}catch{}}}
        static CloudRealtimeStatus Copy(CloudRealtimeStatus x){return new CloudRealtimeStatus{State=x.State,Detail=x.Detail,LastContactUtc=x.LastContactUtc,LastUploadUtc=x.LastUploadUtc,LastSampleUtc=x.LastSampleUtc,HasSubscription=x.HasSubscription,Sent=x.Sent,Failed=x.Failed,Merged=x.Merged};}
        public void Dispose(){lock(sync){if(disposed)return;disposed=true;}CancelBackfill();shutdown.Cancel();try{worker.GetAwaiter().GetResult();}catch{}Task pending;lock(backfillGate)pending=backfillTask;try{if(pending!=null)pending.GetAwaiter().GetResult();}catch{}shutdown.Dispose();}
        internal static int RunSelfTests()
        {
            int addedChecks=0;if(!IsFresh(60,32,0))throw new Exception("60-second sampling period rejected a 32-second sample");addedChecks++;if(IsFresh(2,32,0))throw new Exception("2-second sampling period accepted a 32-second sample");addedChecks++;if(IsFresh(2,29,2))throw new Exception("monotonic pending age was not added to captured sample age");addedChecks++;
            string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".cloud-test");if(Directory.Exists(root))Directory.Delete(root,true);Directory.CreateDirectory(root);try
            {
                CloudConfiguration persisted=new CloudConfiguration{Enabled=true,IncludeSimulation=false,DeviceId="device-test",Alias="lab",Endpoint="https://example.invalid",Token="token-must-not-appear",SavePath=Path.Combine(root,"cloud.txt")};persisted.Save();string raw=File.ReadAllText(persisted.SavePath);if(raw.Contains(persisted.Token))throw new Exception("cloud token stored as clear text");CloudConfiguration loaded=CloudConfiguration.Load(persisted.SavePath,persisted.DeviceId);if(!loaded.Enabled||loaded.Token!=persisted.Token||!loaded.HasValidEndpoint)throw new Exception("cloud DPAPI configuration roundtrip failed");loaded.Endpoint="http://127.0.0.1";if(loaded.HasValidEndpoint)throw new Exception("non-HTTPS cloud endpoint was accepted");
                // 凭据导出/导入：换机与「DPAPI 解不开」时的兜底。四条必须成立：
                // ① 令牌不以明文落盘；② 正确口令可完整往返；③ 错口令被拒；④ 篡改被 MAC 拦下。
                CloudConfiguration exportSource=new CloudConfiguration{Enabled=true,DeviceId="device-export",Endpoint="https://example.invalid",Alias="实验室",Token="secret-token-value",SavePath=Path.Combine(root,"export.txt")};
                string bundle=CloudCredentialBundle.Export(exportSource,"correct horse battery");
                if(bundle.Contains("secret-token-value"))throw new Exception("凭据导出把令牌写成了明文");
                CloudConfiguration imported=CloudCredentialBundle.Import(bundle,"correct horse battery",Path.Combine(root,"imported.txt"));
                if(imported.DeviceId!="device-export"||imported.Token!="secret-token-value"||imported.Endpoint!="https://example.invalid")throw new Exception("凭据导出/导入往返失败");
                bool wrongPasswordRejected=false;try{CloudCredentialBundle.Import(bundle,"totally wrong password",Path.Combine(root,"x.txt"));}catch(InvalidOperationException){wrongPasswordRejected=true;}
                if(!wrongPasswordRejected)throw new Exception("错误口令没有被拒绝");
                string[] bundleLines=bundle.Split('\n');int ctIndex=-1;for(int i=0;i<bundleLines.Length;i++)if(bundleLines[i].StartsWith("ct=",StringComparison.Ordinal)){ctIndex=i;break;}
                if(ctIndex<0)throw new Exception("凭据文件缺少密文字段");
                char[] ctChars=bundleLines[ctIndex].ToCharArray();ctChars[3]=ctChars[3]=='A'?'B':'A';bundleLines[ctIndex]=new string(ctChars);
                bool tamperRejected=false;try{CloudCredentialBundle.Import(String.Join("\n",bundleLines),"correct horse battery",Path.Combine(root,"y.txt"));}catch(InvalidOperationException){tamperRejected=true;}
                if(!tamperRejected)throw new Exception("被篡改的密文没有被 MAC 拦下");
                addedChecks++;
                // 旧配置搬迁：老用户升级后不能丢令牌、不能换身份。
                string legacyConfig=Path.Combine(root,"legacy","cloud-connection.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(legacyConfig));
                new CloudConfiguration{Enabled=true,DeviceId="device-legacy-001",Endpoint="https://example.invalid",Alias="旧机器",Token="legacy-token",SavePath=legacyConfig}.Save();
                string adoptedPath=Path.Combine(root,"newpath","cloud-connection.txt");
                if(!String.Equals(CloudSettingsMigration.AdoptConfig(adoptedPath,legacyConfig),adoptedPath))throw new Exception("配置搬迁返回了意外路径");
                CloudConfiguration adopted=CloudConfiguration.Load(adoptedPath,null);
                if(adopted.DeviceId!="device-legacy-001"||adopted.Token!="legacy-token")throw new Exception("配置搬迁没有保住身份与令牌");
                if(!File.Exists(legacyConfig))throw new Exception("配置搬迁删除了旧文件（旧文件必须保留）");
                // 新位置已经配好令牌时，绝不能被旧配置覆盖。
                string keepPath=Path.Combine(root,"keep","cloud-connection.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(keepPath));
                new CloudConfiguration{DeviceId="device-newer-0001",Endpoint="https://example.invalid",Alias="新",Token="new-token",SavePath=keepPath}.Save();
                CloudSettingsMigration.AdoptConfig(keepPath,legacyConfig);
                if(CloudConfiguration.Load(keepPath,null).Token!="new-token")throw new Exception("配置搬迁覆盖了已配置好的新位置");
                // 旧身份只在旧配置确实配置过令牌时才采纳；空壳配置不许冒充。
                string adoptedId;
                if(!CloudSettingsMigration.TryReadLegacyIdentity(legacyConfig,delegate(string v){return v.Length>=16;},out adoptedId)||adoptedId!="device-legacy-001")throw new Exception("旧配置身份识别失败");
                string emptyLegacy=Path.Combine(root,"emptylegacy","cloud-connection.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(emptyLegacy));
                new CloudConfiguration{DeviceId="device-empty-0001",Endpoint="https://",Alias="",Token="",SavePath=emptyLegacy}.Save();
                string ignoredId;
                // 没有令牌的旧配置同样承载着升级前的编号，身份照样采纳；能不能覆盖目标由 HasToken 决定。
                string emptyId;if(!CloudSettingsMigration.TryReadLegacyIdentity(emptyLegacy,delegate(string v){return v.Length>=16;},out emptyId)||emptyId!="device-empty-0001")throw new Exception("无令牌旧配置的身份未被识别");
                if(CloudSettingsMigration.HasToken(keepPath)!=true)throw new Exception("已配置目标应被识别为已有令牌");
                if(CloudSettingsMigration.HasToken(emptyLegacy)!=false)throw new Exception("空壳配置不该被识别为已有令牌");
                addedChecks++;
                
                FakeCloudTransport fake=new FakeCloudTransport();CloudConfiguration runtime=new CloudConfiguration{Enabled=true,DeviceId="device-test",Alias="lab",Endpoint="https://example.invalid",Token="fake-only"};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},fake))
                {
                    RealtimeSnapshot snapshot=TestSnapshot("serial",DateTime.UtcNow);service.Publish(snapshot);DateTime limit=DateTime.UtcNow.AddSeconds(4);while(fake.Uploads<1&&DateTime.UtcNow<limit)Thread.Sleep(25);if(fake.Uploads!=1)throw new Exception("fake lease did not deliver latest snapshot");CloudSnapshotPost sent=fake.Last;if(sent==null||sent.SubscriptionId!="fake-subscription"||sent.Snapshot.DeviceId!="device-test"||sent.Snapshot.Sequence!=1||sent.Snapshot.Source!="serial")throw new Exception("uploaded snapshot contract fields failed");string missingAlert=SerializeEnvelope(sent.Snapshot);if(!missingAlert.Contains("\"alarmObservationAvailable\":false")||!missingAlert.Contains("\"alarmObservation\":null")||!missingAlert.Contains("\"periodSeconds\":2")||!missingAlert.Contains("\"capturedUtc\":\"" )||!missingAlert.Contains("Z\""))throw new Exception("cloud snapshot omitted UTC interval or explicit missing-alert status");Frame af=Protocol.Decode(Simulator.Respond(1,0x44,1));AlarmSnapshot alarm=AlarmSnapshot.Capture(af,"serial",1,DateTime.UtcNow.AddMinutes(-1),"test alarm",6,2);string withAlert=SerializeEnvelope(CloudSnapshotEnvelope.From(snapshot,"device-test","session",2,alarm));if(!withAlert.Contains("\"observedUtc\":\"")||!withAlert.Contains("\"acquisitionRound\":6")||!withAlert.Contains("\"alarmObservationAvailable\":true")||!withAlert.Contains("Z\""))throw new Exception("cloud alert did not preserve its independent UTC observation metadata");
                    Thread.Sleep(1200);service.Publish(TestSnapshot("serial",DateTime.UtcNow));Thread.Sleep(500);if(fake.Uploads!=1)throw new Exception("expired fake lease continued uploading");if(service.Status.HasSubscription)throw new Exception("expired lease remained active");
                }
                runtime.IncludeSimulation=true;FakeCloudTransport noLease=new FakeCloudTransport{LeaseSeconds=0};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},noLease)){int events=0;service.StatusChanged+=delegate{Interlocked.Increment(ref events);};for(int p=1;p<=16;p++)service.Publish(TestSnapshot("serial",DateTime.UtcNow,p));service.Publish(TestSnapshot("simulation",DateTime.UtcNow,1));Thread.Sleep(600);if(noLease.Uploads!=0)throw new Exception("snapshot uploaded without a viewing lease");if(service.Status.Merged<1)throw new Exception("latest-only cache did not enforce its 16-pack capacity");int stableEvents=events;Thread.Sleep(700);if(events!=stableEvents)throw new Exception("unchanged cloud status repeatedly raised UI notifications");}
                runtime.IncludeSimulation=true;FakeCloudTransport simulationFilter=new FakeCloudTransport{LeaseSeconds=10,BlockFirstHeartbeat=true};CloudConfiguration activeRuntime=runtime;using(CloudRealtimeService service=new CloudRealtimeService(delegate{return activeRuntime;},simulationFilter)){service.Publish(TestSnapshot("simulation",DateTime.UtcNow));DateTime until=DateTime.UtcNow.AddSeconds(3);while(simulationFilter.HeartbeatAttempts==0&&DateTime.UtcNow<until)Thread.Sleep(10);if(simulationFilter.HeartbeatAttempts==0)throw new Exception("simulation filter test did not reach heartbeat");runtime=new CloudConfiguration{Enabled=true,IncludeSimulation=false,DeviceId="device-test",Alias="lab",Endpoint="https://example.invalid",Token="fake-only"};activeRuntime=runtime;simulationFilter.ReleaseFirstHeartbeat();until=DateTime.UtcNow.AddSeconds(3);while(!service.Status.HasSubscription&&DateTime.UtcNow<until)Thread.Sleep(10);Thread.Sleep(400);if(!service.Status.HasSubscription||simulationFilter.Uploads!=0)throw new Exception("simulation candidate was not filtered while a usable lease was active");addedChecks++;}
                runtime.IncludeSimulation=false;FakeCloudTransport staleAck=new FakeCloudTransport{LeaseSeconds=30,BlockFirstSend=true};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},staleAck)){service.Publish(TestSnapshot("serial",DateTime.UtcNow));DateTime attemptLimit=DateTime.UtcNow.AddSeconds(3);while(staleAck.Attempts==0&&DateTime.UtcNow<attemptLimit)Thread.Sleep(10);if(staleAck.Attempts==0)throw new Exception("blocked upload did not start");string oldSession=staleAck.FirstSession;service.ResetCaptureSession();service.Publish(TestSnapshot("serial",DateTime.UtcNow));staleAck.ReleaseFirstSend();DateTime uploadLimit=DateTime.UtcNow.AddSeconds(4);while(staleAck.Uploads<2&&DateTime.UtcNow<uploadLimit)Thread.Sleep(20);if(staleAck.Uploads<2||staleAck.Last==null||staleAck.Last.Snapshot.ConnectionSessionId==oldSession||service.Status.Sent!=1)throw new Exception("old in-flight acknowledgement contaminated the new capture session");}
                addedChecks++;
                FakeCloudTransport fair=new FakeCloudTransport{LeaseSeconds=30,RequestedPacks=new[]{1,2},BlockFirstSend=true};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},fair)){service.Publish(TestSnapshot("serial",DateTime.UtcNow,1));Thread.Sleep(20);service.Publish(TestSnapshot("serial",DateTime.UtcNow,2));DateTime attemptLimit=DateTime.UtcNow.AddSeconds(3);while(fair.Attempts==0&&DateTime.UtcNow<attemptLimit)Thread.Sleep(10);if(fair.Attempts==0)throw new Exception("fairness test upload did not start");service.Publish(TestSnapshot("serial",DateTime.UtcNow,1));fair.ReleaseFirstSend();DateTime uploadLimit=DateTime.UtcNow.AddSeconds(3);while(fair.Uploads<2&&DateTime.UtcNow<uploadLimit)Thread.Sleep(10);if(fair.Uploads<2||fair.UploadedPackAt(0)!=1||fair.UploadedPackAt(1)!=2)throw new Exception("older pending Pack was starved by refreshed Pack 1");}addedChecks++;
                FakeCloudTransport changingLease=new FakeCloudTransport{LeaseSeconds=1};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},changingLease)){service.Publish(TestSnapshot("serial",DateTime.UtcNow));DateTime firstLimit=DateTime.UtcNow.AddSeconds(3);while(changingLease.Uploads<1&&DateTime.UtcNow<firstLimit)Thread.Sleep(10);if(changingLease.Uploads<1)throw new Exception("initial subscription did not bootstrap snapshot");changingLease.SubscriptionId="replacement-subscription";DateTime replacementLimit=DateTime.UtcNow.AddSeconds(6);while(changingLease.SubscriptionUploadCount("replacement-subscription")==0&&DateTime.UtcNow<replacementLimit)Thread.Sleep(20);if(changingLease.SubscriptionUploadCount("replacement-subscription")==0)throw new Exception("changed lease subscription did not bootstrap latest snapshot again");}addedChecks++;
                FakeCloudTransport retry=new FakeCloudTransport{LeaseSeconds=10,FailFirst=true};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},retry)){service.Publish(TestSnapshot("serial",DateTime.UtcNow));DateTime firstLimit=DateTime.UtcNow.AddSeconds(2);while(retry.Attempts<1&&DateTime.UtcNow<firstLimit)Thread.Sleep(25);Thread.Sleep(500);if(retry.Attempts!=1)throw new Exception("failed snapshot was retried faster than its backoff");DateTime retryLimit=DateTime.UtcNow.AddSeconds(5);while(retry.Attempts<2&&DateTime.UtcNow<retryLimit)Thread.Sleep(25);if(retry.Attempts<2||retry.Uploads!=1||retry.FirstSequence!=retry.LastSequence)throw new Exception("snapshot retry changed its id/sequence or failed to recover");}
                FakeCloudTransport warm=new FakeCloudTransport{LeaseSeconds=1,BackfillSeconds=300};Func<string,DateTime,DateTime,int,IList<StoredSample>> warmReader=delegate(string source,DateTime from,DateTime to,int pack){return new List<StoredSample>{new StoredSample{Database="fixture.db",Id=9,Source="serial",HistorySessionId="",AcquisitionRound=3,PeriodSeconds=2,ReceivedUtc=DateTime.UtcNow.AddMinutes(-1),Address=1,Pack=pack,Soc=66,Voltage=52.34,Current=-1.25,Cells=new int[0],Temperatures=new int[0]}};};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},warm,warmReader)){DateTime until=DateTime.UtcNow.AddSeconds(3);while(warm.Backfills<1&&DateTime.UtcNow<until)Thread.Sleep(20);if(warm.Backfills!=1||warm.LastBackfill==null||warm.LastBackfill.Points.Length!=1||warm.LastBackfill.Points[0].ConnectionSessionId!="bms-history-fixture"||warm.LastBackfill.Points[0].VoltageCentivolts!=5234)throw new Exception("5-minute BMS warm backfill did not preserve local history or fallback segment");until=DateTime.UtcNow.AddSeconds(4);while(warm.HeartbeatAttempts<2&&DateTime.UtcNow<until)Thread.Sleep(20);if(warm.Backfills!=1)throw new Exception("same subscription repeated BMS warm backfill");warm.SubscriptionId="replacement-backfill";until=DateTime.UtcNow.AddSeconds(4);while(warm.Backfills<2&&DateTime.UtcNow<until)Thread.Sleep(20);if(warm.Backfills!=2||warm.LastBackfill.SubscriptionId!="replacement-backfill")throw new Exception("new subscription did not trigger BMS warm backfill again");}addedChecks++;
                FakeCloudTransport backfillRetry=new FakeCloudTransport{LeaseSeconds=1,BackfillSeconds=300,FailFirstBackfill=true};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},backfillRetry,warmReader)){DateTime until=DateTime.UtcNow.AddSeconds(6);while(backfillRetry.BackfillAttempts<2&&DateTime.UtcNow<until)Thread.Sleep(20);if(backfillRetry.BackfillAttempts<2||backfillRetry.Backfills!=1)throw new Exception("failed BMS warm backfill was not retried on the same subscription");}addedChecks++;
                FakeCloudTransport backfillPriority=new FakeCloudTransport{LeaseSeconds=10,BackfillSeconds=300,BlockBackfill=true};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},backfillPriority,warmReader)){service.Publish(TestSnapshot("serial",DateTime.UtcNow));DateTime until=DateTime.UtcNow.AddSeconds(4);while(backfillPriority.BackfillAttempts<1&&DateTime.UtcNow<until)Thread.Sleep(20);if(backfillPriority.BackfillAttempts<1)throw new Exception("BMS background backfill did not start");if(backfillPriority.Uploads!=1)throw new Exception("blocked BMS history backfill delayed the latest realtime snapshot");backfillPriority.ReleaseBackfill();until=DateTime.UtcNow.AddSeconds(4);while(backfillPriority.Backfills<1&&DateTime.UtcNow<until)Thread.Sleep(20);if(backfillPriority.Backfills!=1)throw new Exception("BMS background backfill did not complete after release");}addedChecks++;
                FakeCloudTransport malformed=new FakeCloudTransport{Reply=new CloudHeartbeatReply{SubscriptionId="bad",LeaseSeconds=30,RequestedPacks=new[]{17}}};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},malformed)){DateTime until=DateTime.UtcNow.AddSeconds(2);while(service.Status.State!="协议错误"&&DateTime.UtcNow<until)Thread.Sleep(25);if(service.Status.State!="协议错误")throw new Exception("invalid lease response was not rejected");}
                FakeCloudTransport unauthorized=new FakeCloudTransport{HeartbeatFailure=new CloudHttpException(401,"fake unauthorized")};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},unauthorized)){DateTime until=DateTime.UtcNow.AddSeconds(2);while(service.Status.State!="认证失败"&&DateTime.UtcNow<until)Thread.Sleep(25);if(service.Status.State!="认证失败")throw new Exception("401 did not produce authentication failure state");}
                FakeCloudTransport addressIsolation=new FakeCloudTransport{LeaseSeconds=0};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},addressIsolation)){service.Publish(TestSnapshot("serial",DateTime.UtcNow,1,1));service.Publish(TestSnapshot("serial",DateTime.UtcNow,1,2));Thread.Sleep(200);if(service.Status.Merged!=0)throw new Exception("different address same pack collided into merged drop");service.Publish(TestSnapshot("serial",DateTime.UtcNow,1,1));Thread.Sleep(200);if(service.Status.Merged!=1)throw new Exception("same address same pack was not merged");addedChecks++;}
                return 9+addedChecks+CloudTransportTests.Run();
            }
            finally{string full=Path.GetFullPath(root),basePath=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);if(full.StartsWith(basePath,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(full))Directory.Delete(full,true);}
        }
        static RealtimeSnapshot TestSnapshot(string source,DateTime time,int pack=1,byte address=1){Frame f=Protocol.Decode(Simulator.Respond(address,0x42,(byte)pack));string layout;List<PackData> data=DataParser.Realtime(f.Info,(byte)pack,out layout);return RealtimeSnapshot.Capture(f,data[0],source,time,null,7,2);}
        static string SerializeEnvelope(CloudSnapshotEnvelope envelope){using(MemoryStream ms=new MemoryStream()){new DataContractJsonSerializer(typeof(CloudSnapshotEnvelope)).WriteObject(ms,envelope);return Encoding.UTF8.GetString(ms.ToArray());}}
        sealed class FakeCloudTransport:ICloudTransport
        {
            public int Uploads,Attempts,HeartbeatAttempts,Backfills,BackfillAttempts;public int LeaseSeconds=1,BackfillSeconds=0;public bool FailFirst,FailFirstBackfill,BlockBackfill,BlockFirstSend,BlockFirstHeartbeat;public int[] RequestedPacks=new[]{1};public string SubscriptionId="fake-subscription";public long FirstSequence,LastSequence;public string FirstSession;public CloudBackfillPost LastBackfill;readonly TaskCompletionSource<object> releaseFirst=new TaskCompletionSource<object>(),releaseHeartbeat=new TaskCompletionSource<object>(),releaseBackfill=new TaskCompletionSource<object>();readonly object historyLock=new object();readonly List<CloudSnapshotPost> history=new List<CloudSnapshotPost>();public CloudSnapshotPost Last;public CloudHeartbeatReply Reply;public Exception HeartbeatFailure;
            public async Task<CloudHeartbeatReply> HeartbeatAsync(CloudConfiguration c,CancellationToken t){int attempt=Interlocked.Increment(ref HeartbeatAttempts);if(BlockFirstHeartbeat&&attempt==1)await releaseHeartbeat.Task.ConfigureAwait(false);if(HeartbeatFailure!=null){TaskCompletionSource<CloudHeartbeatReply> failed=new TaskCompletionSource<CloudHeartbeatReply>();failed.SetException(HeartbeatFailure);return await failed.Task.ConfigureAwait(false);}return Reply??new CloudHeartbeatReply{SubscriptionId=LeaseSeconds>0?SubscriptionId:"",LeaseSeconds=LeaseSeconds,RequestedPacks=RequestedPacks,BackfillSeconds=BackfillSeconds};}
            public Task SendSnapshotAsync(CloudConfiguration c,CloudSnapshotPost post,CancellationToken t){Last=post;int attempt=Interlocked.Increment(ref Attempts);if(attempt==1){FirstSequence=post.Snapshot.Sequence;FirstSession=post.Snapshot.ConnectionSessionId;}LastSequence=post.Snapshot.Sequence;if(FailFirst&&attempt==1){TaskCompletionSource<object> failed=new TaskCompletionSource<object>();failed.SetException(new IOException("fake temporary failure"));return failed.Task;}if(BlockFirstSend&&attempt==1)return CompleteAfterRelease(post);Record(post);return Task.FromResult(0);}
            public Task SendBackfillAsync(CloudConfiguration c,CloudBackfillPost post,CancellationToken t){LastBackfill=post;int attempt=Interlocked.Increment(ref BackfillAttempts);if(FailFirstBackfill&&attempt==1){TaskCompletionSource<object> failed=new TaskCompletionSource<object>();failed.SetException(new IOException("fake backfill failure"));return failed.Task;}if(BlockBackfill)return CompleteBackfillAfterRelease(post,t);Interlocked.Increment(ref Backfills);return Task.FromResult(0);}
            async Task CompleteBackfillAfterRelease(CloudBackfillPost post,CancellationToken t){using(t.Register(delegate{releaseBackfill.TrySetCanceled();})){await releaseBackfill.Task.ConfigureAwait(false);}LastBackfill=post;Interlocked.Increment(ref Backfills);}
            public void ReleaseBackfill(){releaseBackfill.TrySetResult(null);}
            async Task CompleteAfterRelease(CloudSnapshotPost post){await releaseFirst.Task.ConfigureAwait(false);Last=post;Record(post);}
            public void ReleaseFirstSend(){releaseFirst.TrySetResult(null);}
            public void ReleaseFirstHeartbeat(){releaseHeartbeat.TrySetResult(null);}
            void Record(CloudSnapshotPost post){lock(historyLock)history.Add(post);Interlocked.Increment(ref Uploads);}
            public int UploadedPackAt(int index){lock(historyLock)return index<history.Count?history[index].Snapshot.Pack:0;}
            public int SubscriptionUploadCount(string id){lock(historyLock){int count=0;foreach(CloudSnapshotPost post in history)if(post.SubscriptionId==id)count++;return count;}}
        }
    }
}
