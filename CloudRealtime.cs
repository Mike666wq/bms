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

namespace BmsSerialDemo
{
    public sealed class CloudConfiguration
    {
        public bool Enabled { get; set; }
        public bool IncludeSimulation { get; set; }
        public string DeviceId { get; set; }
        public string Alias { get; set; }
        public string Endpoint { get; set; }
        public string Token { get; set; }
        public string SavePath { get; set; }
        internal bool SavedTokenUnreadable;
        public static CloudConfiguration Load(string path,string deviceId)
        {
            // 配置文件读取/解析失败不得阻断启动（例如只读目录、文件被占用、内容损坏）：记录诊断日志并回退到默认配置。
            CloudConfiguration c=new CloudConfiguration{DeviceId=deviceId,Endpoint="https://",Token="",Alias="",SavePath=path};
            if(!File.Exists(path))return c;
            Dictionary<string,string> map;
            try
            {
                map=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(string line in File.ReadAllLines(path,Encoding.UTF8)){int i=line.IndexOf('=');if(i>0)map[line.Substring(0,i)]=line.Substring(i+1);}
            }
            catch(Exception e){CrashLogger.Write("云端连接配置读取失败，已使用默认配置："+path,e);return c;}
            try
            {
                string value;if(map.TryGetValue("enabled",out value))c.Enabled=value=="1";if(map.TryGetValue("simulation",out value))c.IncludeSimulation=value=="1";if(map.TryGetValue("alias",out value))try{c.Alias=Encoding.UTF8.GetString(Convert.FromBase64String(value));}catch{}if(map.TryGetValue("endpoint",out value))try{c.Endpoint=Encoding.UTF8.GetString(Convert.FromBase64String(value));}catch{}
                if(map.TryGetValue("token",out value)&&value.Length>0)try{c.Token=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),null,DataProtectionScope.CurrentUser));}catch{c.Token="";c.SavedTokenUnreadable=true;}
            }
            catch(Exception e){CrashLogger.Write("云端连接配置解析异常，已部分采用默认值："+path,e);}
            return c;
        }
        public void Save()
        {
            // C24：无目录部分直接报错；失败路径清理 .partial-* 临时文件（含 DPAPI 密文）后重抛。
            if(String.IsNullOrWhiteSpace(SavePath))throw new InvalidOperationException("配置路径无效");
            string parentDirectory=Path.GetDirectoryName(SavePath);if(String.IsNullOrEmpty(parentDirectory))throw new InvalidOperationException("配置路径必须包含目录部分");
            Directory.CreateDirectory(parentDirectory);string secret="";if(!String.IsNullOrEmpty(Token))secret=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Token),null,DataProtectionScope.CurrentUser));
            string text="enabled="+(Enabled?"1":"0")+"\r\nsimulation="+(IncludeSimulation?"1":"0")+"\r\nendpoint="+B64(Endpoint)+"\r\nalias="+B64(Alias)+"\r\ntoken="+secret+"\r\n";string temp=SavePath+".partial-"+Guid.NewGuid().ToString("N");
            try{File.WriteAllText(temp,text,new UTF8Encoding(false));if(File.Exists(SavePath))File.Replace(temp,SavePath,null);else File.Move(temp,SavePath);}
            catch{try{if(File.Exists(temp))File.Delete(temp);}catch{}throw;}
        }
        static string B64(string s){return Convert.ToBase64String(Encoding.UTF8.GetBytes(s??""));}
        public bool HasValidEndpoint { get { Uri u;return Uri.TryCreate(Endpoint,UriKind.Absolute,out u)&&u.Scheme==Uri.UriSchemeHttps&&String.IsNullOrEmpty(u.UserInfo)&&String.IsNullOrEmpty(u.Fragment)&&String.IsNullOrEmpty(u.Query); } }
    }
    [DataContract]
    public sealed class CloudHeartbeatReply
    {
        [DataMember(Name="subscriptionId",IsRequired=true)]public string SubscriptionId{get;set;}
        [DataMember(Name="leaseSeconds",IsRequired=true)]public int LeaseSeconds{get;set;}
        [DataMember(Name="requestedPacks",IsRequired=true)]public int[] RequestedPacks{get;set;}
        [DataMember(Name="backfillSeconds",EmitDefaultValue=false)]public int BackfillSeconds{get;set;}
    }
    [DataContract]
    sealed class CloudHeartbeatRequest
    {
        [DataMember(Name="deviceId")]public string DeviceId{get;set;}
        [DataMember(Name="alias")]public string Alias{get;set;}
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
    [DataContract]
    sealed class CloudAcknowledgement{[DataMember(Name="accepted")]public bool Accepted{get;set;}}
    public sealed class CloudHttpException:Exception
    {
        public int StatusCode{get;private set;}
        public CloudHttpException(int status,string message):base(message){StatusCode=status;}
    }
    internal static class CloudNetworkErrors
    {
        static readonly object logLock=new object();
        static readonly Dictionary<string,long> lastWrittenTick=new Dictionary<string,long>(StringComparer.Ordinal);
        const int MaxLogBytes=256*1024;
        internal static string Describe(Exception error)
        {
            if(error==null)return "网络错误：未知异常";
            CloudHttpException http=error as CloudHttpException;if(http!=null)return DescribeHttp(http.StatusCode);
            TimeoutException timeout=error as TimeoutException;if(timeout!=null)return "网络错误：请求超时";
            WebException web=error as WebException;
            if(web!=null)
            {
                string category;
                switch(web.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure:case WebExceptionStatus.ProxyNameResolutionFailure:category="DNS解析失败";break;
                    case WebExceptionStatus.ConnectFailure:category=SocketCategory(web.InnerException)??(web.InnerException is AuthenticationException?"TLS握手失败":"无法连接服务器");break;
                    case WebExceptionStatus.TrustFailure:category="TLS证书验证失败";break;
                    case WebExceptionStatus.SecureChannelFailure:category="TLS握手失败";break;
                    case WebExceptionStatus.Timeout:category="请求超时";break;
                    case WebExceptionStatus.ProtocolError:
                        HttpWebResponse response=web.Response as HttpWebResponse;category=response!=null?DescribeHttp((int)response.StatusCode):"HTTP协议响应失败";break;
                    default:
                        category=SocketCategory(web.InnerException);
                        if(category==null)category=web.InnerException is AuthenticationException?"TLS握手失败":WebCategory(web.Status);
                        break;
                }
                return "网络错误："+category+"（WebExceptionStatus="+web.Status+"/HResult=0x"+web.HResult.ToString("X8")+""+InnerIdentitySuffix(web.InnerException)+"）";
            }
            string socket=SocketCategory(error);if(socket!=null)return "网络错误："+socket+"（"+KnownIdentity(error)+"）";
            if(error is AuthenticationException)return "网络错误：TLS握手失败（"+KnownIdentity(error)+"）";
            if(error is InvalidDataException||error is System.Runtime.Serialization.SerializationException)return "协议错误：云端数据格式无效";
            if(error is IOException)return "网络错误：I/O请求失败（"+KnownIdentity(error)+"）";
            return "网络或协议错误："+KnownIdentity(error);
        }
        static string DescribeHttp(int status)
        {if(status==407)return "网络错误：代理要求认证（HTTP 407）";if(status==401||status==403)return "云端认证失败（HTTP "+status+"）";return "网络错误：HTTP响应失败（HTTP "+status+"）";}
        static string WebCategory(WebExceptionStatus status){return status==WebExceptionStatus.ProxyNameResolutionFailure?"DNS解析失败":status==WebExceptionStatus.ConnectFailure?"连接失败":status==WebExceptionStatus.TrustFailure?"TLS证书验证失败":status==WebExceptionStatus.SecureChannelFailure?"TLS握手失败":status==WebExceptionStatus.Timeout?"请求超时":status==WebExceptionStatus.ProtocolError?"HTTP协议响应失败":"请求失败";}
        static string SocketCategory(Exception e){while(e!=null){SocketException socket=e as SocketException;if(socket!=null){switch(socket.SocketErrorCode){case SocketError.HostNotFound:case SocketError.TryAgain:case SocketError.NoRecovery:case SocketError.NoData:return "DNS解析失败";case SocketError.ConnectionRefused:return "连接被拒绝";case SocketError.TimedOut:return "连接超时";default:return null;}}e=e.InnerException;}return null;}
        static string InnerIdentitySuffix(Exception e){return e==null?"":"; Inner="+KnownIdentity(e);}
        static string KnownIdentity(Exception e){if(e==null)return "异常类型未知";string type=e.GetType()==typeof(SocketException)?"SocketException":e.GetType()==typeof(AuthenticationException)?"AuthenticationException":e.GetType()==typeof(IOException)?"IOException":e.GetType()==typeof(TimeoutException)?"TimeoutException":e.GetType()==typeof(WebException)?"WebException":e.GetType()==typeof(InvalidDataException)?"InvalidDataException":e.GetType()==typeof(System.Runtime.Serialization.SerializationException)?"SerializationException":"Exception";return type+"/HResult=0x"+e.HResult.ToString("X8");}
        internal static void Write(Exception error)
        {
            string description=Describe(error),category=description;int p=description.IndexOf('：');if(p>=0)category=description.Substring(p+1);p=category.IndexOf('（');if(p>=0)category=category.Substring(0,p);long tick=Stopwatch.GetTimestamp();
            lock(logLock)
            {
                long previousTick;if(lastWrittenTick.TryGetValue(category,out previousTick)&&tick-previousTick<Stopwatch.Frequency*60L)return;lastWrittenTick[category]=tick;
                try
                {
                    string line=DateTimeOffset.Now.ToString("o")+" "+description+Environment.NewLine;byte[] bytes=new UTF8Encoding(false).GetBytes(line);
                    if(!AppendLog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs"),bytes))
                    {string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);if(!String.IsNullOrEmpty(local))AppendLog(Path.Combine(local,"BmsSerialDemo","logs"),bytes);}
                }catch{}
            }
        }
        static bool AppendLog(string directory,byte[] bytes)
        {try{Directory.CreateDirectory(directory);string path=Path.Combine(directory,"cloud-network-errors.log"),backup=path+".1";if(File.Exists(path)&&new FileInfo(path).Length+bytes.Length>MaxLogBytes){if(File.Exists(backup))File.Delete(backup);File.Move(path,backup);}using(FileStream f=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read))f.Write(bytes,0,bytes.Length);return true;}catch{return false;}}
    }
    public interface ICloudTransport
    {
        Task<CloudHeartbeatReply> HeartbeatAsync(CloudConfiguration configuration,CancellationToken token);
        Task SendSnapshotAsync(CloudConfiguration configuration,CloudSnapshotPost post,CancellationToken token);
        Task SendBackfillAsync(CloudConfiguration configuration,CloudBackfillPost post,CancellationToken token);
    }
    public sealed class HttpsCloudTransport:ICloudTransport
    {
        static readonly DataContractJsonSerializer heartbeatSerializer=new DataContractJsonSerializer(typeof(CloudHeartbeatRequest));
        static readonly DataContractJsonSerializer replySerializer=new DataContractJsonSerializer(typeof(CloudHeartbeatReply));
        static readonly DataContractJsonSerializer snapshotSerializer=new DataContractJsonSerializer(typeof(CloudSnapshotPost));
        static readonly DataContractJsonSerializer acknowledgementSerializer=new DataContractJsonSerializer(typeof(CloudAcknowledgement));
        readonly Func<Uri,HttpWebRequest> requestFactory;
        readonly bool testLoopback;
        readonly int timeoutMilliseconds;
        public HttpsCloudTransport(){timeoutMilliseconds=5000;}
        internal HttpsCloudTransport(Func<Uri,HttpWebRequest> factory,int timeoutMilliseconds,bool testLoopback)
        {if(factory==null)throw new ArgumentNullException("factory");if(timeoutMilliseconds<1||timeoutMilliseconds>5000)throw new ArgumentOutOfRangeException("timeoutMilliseconds");requestFactory=factory;this.timeoutMilliseconds=timeoutMilliseconds;this.testLoopback=testLoopback;}
        public Task<CloudHeartbeatReply> HeartbeatAsync(CloudConfiguration configuration,CancellationToken token)
        {CloudHeartbeatRequest request=new CloudHeartbeatRequest{DeviceId=configuration.DeviceId,Alias=configuration.Alias};return SendAsync<CloudHeartbeatReply>(configuration,"/api/realtime/heartbeat",request,replySerializer,token);}
        public async Task SendSnapshotAsync(CloudConfiguration configuration,CloudSnapshotPost post,CancellationToken token)
        {CloudAcknowledgement a=await SendAsync<CloudAcknowledgement>(configuration,"/api/realtime/snapshots",post,acknowledgementSerializer,token).ConfigureAwait(false);if(a==null||!a.Accepted)throw new InvalidDataException("云端未确认快照");}
        public async Task SendBackfillAsync(CloudConfiguration configuration,CloudBackfillPost post,CancellationToken token)
        {CloudAcknowledgement a=await SendAsync<CloudAcknowledgement>(configuration,"/api/realtime/backfill",post,acknowledgementSerializer,token).ConfigureAwait(false);if(a==null||!a.Accepted)throw new InvalidDataException("云端未确认历史回填");}
        async Task<T> SendAsync<T>(CloudConfiguration c,string route,object payload,DataContractJsonSerializer responseSerializer,CancellationToken token)
        {
            Uri uri=BuildUri(c,route);HttpWebRequest req=requestFactory==null?(HttpWebRequest)WebRequest.Create(uri):requestFactory(uri);if(requestFactory!=null){IPAddress injectedIp;if(req==null||req.RequestUri==null||!IPAddress.TryParse(req.RequestUri.Host,out injectedIp)||!IPAddress.IsLoopback(injectedIp))throw new InvalidOperationException("测试传输仅允许 loopback 请求");}req.Method="POST";req.ContentType="application/json; charset=utf-8";req.Accept="application/json";req.Timeout=timeoutMilliseconds;req.ReadWriteTimeout=timeoutMilliseconds;req.AllowAutoRedirect=false;req.Proxy=requestFactory==null?WebRequest.DefaultWebProxy:null;req.Headers[HttpRequestHeader.Authorization]="Bearer "+(c.Token??"");
            using(CancellationTokenSource deadline=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(timeoutMilliseconds);using(deadline.Token.Register(delegate{try{req.Abort();}catch{}}))
                {
                    try
                    {
                        byte[] bytes;using(MemoryStream payloadStream=new MemoryStream()){new DataContractJsonSerializer(payload.GetType()).WriteObject(payloadStream,payload);bytes=payloadStream.ToArray();}
                        using(Stream body=await req.GetRequestStreamAsync().ConfigureAwait(false)){await body.WriteAsync(bytes,0,bytes.Length,deadline.Token).ConfigureAwait(false);}
                        using(HttpWebResponse response=(HttpWebResponse)await req.GetResponseAsync().ConfigureAwait(false))
                        {
                            int status=(int)response.StatusCode;if(status<200||status>=300)throw new CloudHttpException(status,status==401||status==403?"云端认证失败":"云端 HTTP "+status);
                            if(response.ContentLength>65536)throw new InvalidDataException("云端响应超过64 KiB");using(Stream stream=response.GetResponseStream()){if(stream==null)throw new InvalidDataException("云端响应为空");using(MemoryStream ms=new MemoryStream()){byte[] block=new byte[4096];int n,total=0;while((n=await stream.ReadAsync(block,0,block.Length,deadline.Token).ConfigureAwait(false))>0){total+=n;if(total>65536)throw new InvalidDataException("云端响应超过64 KiB");ms.Write(block,0,n);}if(total==0)throw new InvalidDataException("云端响应为空");ms.Position=0;return (T)responseSerializer.ReadObject(ms);}}
                        }
                    }
                    catch(WebException e)
                    {
                        HttpWebResponse response=e.Response as HttpWebResponse;if(response!=null){int status=(int)response.StatusCode;response.Close();throw new CloudHttpException(status,status==401||status==403?"云端认证失败":"云端 HTTP "+status);}
                        if(deadline.IsCancellationRequested){if(token.IsCancellationRequested)throw new OperationCanceledException("云端请求已取消",e,token);throw new TimeoutException("云端请求超过"+timeoutMilliseconds+"毫秒时限",e);}throw;
                    }
                    catch(OperationCanceledException e){if(token.IsCancellationRequested)throw new OperationCanceledException("云端请求已取消",e,token);throw new TimeoutException("云端请求超过"+timeoutMilliseconds+"毫秒时限",e);}
                }
            }
        }
        Uri BuildUri(CloudConfiguration c,string route)
        {Uri u;if(c==null||!Uri.TryCreate(c.Endpoint,UriKind.Absolute,out u))throw new InvalidOperationException("仅允许 HTTPS 服务地址");if(!c.HasValidEndpoint){IPAddress ip;if(!testLoopback||u.Scheme!=Uri.UriSchemeHttp||!IPAddress.TryParse(u.Host,out ip)||!IPAddress.IsLoopback(ip))throw new InvalidOperationException("仅允许 HTTPS 服务地址");}return new Uri(new Uri(c.Endpoint.TrimEnd('/')+"/"),route.TrimStart('/'));}
    }
    public sealed class CloudRealtimeStatus
    {
        public string State{get;internal set;}public string Detail{get;internal set;}public DateTime? LastContactUtc{get;internal set;}public DateTime? LastUploadUtc{get;internal set;}public DateTime? LastSampleUtc{get;internal set;}public bool HasSubscription{get;internal set;}public long Sent{get;internal set;}public long Failed{get;internal set;}public long Merged{get;internal set;}
    }
    public sealed class CloudRealtimeService: IRealtimePublisher,IDisposable
    {
        sealed class Pending{public RealtimeSnapshot Snapshot;public long Sequence;public long UpdatedTimestamp;public double CapturedAgeSeconds;}
        readonly object sync=new object(),backfillGate=new object();readonly Func<CloudConfiguration> configuration;readonly ICloudTransport transport;readonly Func<string,DateTime,DateTime,int,IList<StoredSample>> historyReader;readonly CancellationTokenSource shutdown=new CancellationTokenSource();readonly Dictionary<string,Pending> latest=new Dictionary<string,Pending>(StringComparer.Ordinal);readonly Dictionary<string,long> sent=new Dictionary<string,long>(StringComparer.Ordinal);string sessionId=Guid.NewGuid().ToString("N"),backfilledSubscription=null,backfillRunningSubscription=null;long sessionGeneration;readonly Task worker;Task backfillTask=Task.FromResult(0);CancellationTokenSource backfillCancel;readonly Random jitter=new Random();CloudRealtimeStatus status=new CloudRealtimeStatus{State="未启用",Detail="云端连接默认关闭"};long sequence,merged,notifiedSent,notifiedFailed;bool disposed;DateTime? notifiedContact,notifiedUpload;
        public event EventHandler StatusChanged;
        public CloudRealtimeStatus Status{get{lock(sync)return Copy(status);}}
        public CloudRealtimeService(Func<CloudConfiguration> getConfiguration,ICloudTransport cloudTransport=null,Func<string,DateTime,DateTime,int,IList<StoredSample>> readHistory=null){configuration=getConfiguration;transport=cloudTransport??new HttpsCloudTransport();historyReader=readHistory;worker=Task.Run((Func<Task>)RunAsync);}
        public void Publish(RealtimeSnapshot snapshot)
        {
            if(snapshot==null||snapshot.Pack<1||snapshot.Pack>16)return;lock(sync){if(disposed)return;CloudConfiguration c;try{c=configuration();}catch{return;}if(c==null||!c.Enabled||(snapshot.Source=="simulation"&&!c.IncludeSimulation))return;string key=snapshot.Source+":"+snapshot.Pack;Pending old;if(latest.TryGetValue(key,out old))merged++;else if(latest.Count>=16){string drop=null;long oldest=Int64.MaxValue;foreach(KeyValuePair<string,Pending> pair in latest)if(pair.Value.UpdatedTimestamp<oldest){oldest=pair.Value.UpdatedTimestamp;drop=pair.Key;}if(drop!=null){latest.Remove(drop);sent.Remove(drop);merged++;}}DateTime now=DateTime.UtcNow;latest[key]=new Pending{Snapshot=snapshot,Sequence=++sequence,UpdatedTimestamp=Stopwatch.GetTimestamp(),CapturedAgeSeconds=Math.Max(0,(now-snapshot.ReceivedUtc.ToUniversalTime()).TotalSeconds)};status.Merged=merged;} }
        public void ResetCaptureSession(){lock(sync){if(disposed)return;latest.Clear();sent.Clear();sequence=0;status.LastSampleUtc=null;status.LastUploadUtc=null;sessionId=Guid.NewGuid().ToString("N");sessionGeneration++;}}
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
        async Task RunAsync()
        {
            Stopwatch scheduler=Stopwatch.StartNew();long nextHeartbeatAt=0,nextUploadAttemptAt=0;int backoff=1;bool uploadRequest=false,uploadHasFailed=false;Stopwatch lease=Stopwatch.StartNew();long leaseDeadline=0;string subscription=null;HashSet<int> requested=new HashSet<int>();CloudConfiguration activeConfiguration=null;
            while(!shutdown.IsCancellationRequested)
            {
                CloudConfiguration c=null;try{c=configuration();}catch{}if(c==null||!c.Enabled){CancelBackfill();lock(backfillGate)backfilledSubscription=null;subscription=null;leaseDeadline=0;activeConfiguration=null;lock(sync){latest.Clear();sent.Clear();}Update("未启用","云端连接默认关闭",false);await Delay(1000);continue;}if(!ReferenceEquals(activeConfiguration,c)){CancelBackfill();lock(backfillGate)backfilledSubscription=null;activeConfiguration=c;subscription=null;leaseDeadline=0;nextHeartbeatAt=0;nextUploadAttemptAt=0;requested.Clear();}if(!c.HasValidEndpoint||String.IsNullOrWhiteSpace(c.Token)){Update("等待配置","请输入 HTTPS 服务地址和设备令牌",false);await Delay(1000);continue;}
                try
                {
                    if(scheduler.ElapsedMilliseconds>=nextHeartbeatAt){uploadRequest=false;CloudHeartbeatReply reply=await transport.HeartbeatAsync(c,shutdown.Token).ConfigureAwait(false);if(reply==null||reply.LeaseSeconds<0||reply.LeaseSeconds>3600||reply.BackfillSeconds<0||reply.BackfillSeconds>300||reply.RequestedPacks==null||reply.RequestedPacks.Length>16||(reply.LeaseSeconds>0&&String.IsNullOrWhiteSpace(reply.SubscriptionId)))throw new InvalidDataException("心跳租约响应字段无效");int[] packs=reply.RequestedPacks;foreach(int pack in packs)if(pack<1||pack>16)throw new InvalidDataException("心跳租约包含无效 Pack");lock(sync){status.LastContactUtc=DateTime.UtcNow;}nextHeartbeatAt=scheduler.ElapsedMilliseconds+(reply.LeaseSeconds>0?Math.Min(15000,Math.Max(2000,reply.LeaseSeconds*500)):15000);if(!uploadHasFailed)backoff=1;string previousSubscription=subscription;subscription=reply.SubscriptionId;if(!String.Equals(previousSubscription,subscription,StringComparison.Ordinal)){lock(sync)sent.Clear();nextUploadAttemptAt=0;}requested=new HashSet<int>(packs);if(!String.IsNullOrEmpty(subscription)&&reply.LeaseSeconds>0){lease.Restart();leaseDeadline=(long)reply.LeaseSeconds*1000;Update("在线等待观看","收到观看租约",true);ScheduleWarmBackfill(c,reply,requested,subscription);}else{CancelBackfill();leaseDeadline=0;Update("在线等待观看","暂无观看订阅，仅发送心跳",false);}}
                    if(leaseDeadline>0&&lease.ElapsedMilliseconds<leaseDeadline&&subscription!=null){KeyValuePair<string,Pending>? candidate=null;string uploadSession=null;long uploadGeneration=0;if(scheduler.ElapsedMilliseconds>=nextUploadAttemptAt)lock(sync){CloudConfiguration current;try{current=configuration();}catch{current=null;}bool configurationCurrent=current!=null&&current.Enabled&&current.HasValidEndpoint&&!String.IsNullOrWhiteSpace(current.Token)&&ReferenceEquals(current,c);if(configurationCurrent){long now=Stopwatch.GetTimestamp();long oldest=Int64.MaxValue;foreach(KeyValuePair<string,Pending> pair in latest){Pending p=pair.Value;int period=p.Snapshot.PeriodSeconds.HasValue?p.Snapshot.PeriodSeconds.Value:0;double elapsed=(now-p.UpdatedTimestamp)/(double)Stopwatch.Frequency;if(!IsFresh(period,p.CapturedAgeSeconds,elapsed)||!requested.Contains(p.Snapshot.Pack)||(p.Snapshot.Source=="simulation"&&!current.IncludeSimulation))continue;long sentSeq;if((!sent.TryGetValue(pair.Key,out sentSeq)||sentSeq!=p.Sequence)&&p.UpdatedTimestamp<oldest){oldest=p.UpdatedTimestamp;candidate=pair;}}}if(candidate.HasValue){uploadSession=sessionId;uploadGeneration=sessionGeneration;}}if(candidate.HasValue){uploadRequest=true;Pending p=candidate.Value.Value;CloudSnapshotEnvelope envelope=CloudSnapshotEnvelope.From(p.Snapshot,c.DeviceId,uploadSession,p.Sequence);await transport.SendSnapshotAsync(c,new CloudSnapshotPost{SubscriptionId=subscription,Snapshot=envelope},shutdown.Token).ConfigureAwait(false);uploadRequest=false;uploadHasFailed=false;bool currentSession=false;lock(sync){Pending current;if(sessionGeneration==uploadGeneration&&sessionId==uploadSession&&latest.TryGetValue(candidate.Value.Key,out current)&&current.Sequence==p.Sequence){sent[candidate.Value.Key]=p.Sequence;status.Sent++;status.LastUploadUtc=DateTime.UtcNow;status.LastSampleUtc=p.Snapshot.ReceivedUtc;currentSession=true;}}if(currentSession)Update("上传中","最近快照已确认",true);backoff=1;nextUploadAttemptAt=0;continue;}Update("在线等待观看",scheduler.ElapsedMilliseconds<nextUploadAttemptAt?"上传失败，等待重试":"租约有效；没有可上传的新鲜快照",true);}else if(leaseDeadline>0){leaseDeadline=0;Update("在线等待观看","观看租约已过期，上传已暂停",false);}
                }
                catch(OperationCanceledException){if(shutdown.IsCancellationRequested)break;lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;Update("离线重试","云端请求已取消；本地记录继续工作",false);long wait=1000L*backoff;if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait;else nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait;backoff=Math.Min(60,backoff*2);}
                catch(CloudHttpException e){CloudNetworkErrors.Write(e);lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;if(e.StatusCode==401||e.StatusCode==403){CancelBackfill();leaseDeadline=0;subscription=null;Update("认证失败",CloudNetworkErrors.Describe(e)+"；请检查设备令牌",false);nextHeartbeatAt=scheduler.ElapsedMilliseconds+60000;nextUploadAttemptAt=nextHeartbeatAt;}else{Update("离线重试",CloudNetworkErrors.Describe(e)+"；本地记录继续工作",false);int wait=backoff+jitter.Next(0,Math.Max(1,backoff));if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait*1000L;else{nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait*1000L;nextUploadAttemptAt=nextHeartbeatAt;}backoff=Math.Min(60,backoff*2);}}
                catch(TimeoutException e){CloudNetworkErrors.Write(e);lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;Update("离线重试",CloudNetworkErrors.Describe(e)+"；本地记录继续工作",false);long wait=(backoff+jitter.Next(0,Math.Max(1,backoff)))*1000L;if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait;else{nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait;nextUploadAttemptAt=nextHeartbeatAt;}backoff=Math.Min(60,backoff*2);}
                catch(WebException e){CloudNetworkErrors.Write(e);lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;Update("离线重试",CloudNetworkErrors.Describe(e)+"；本地记录继续工作",false);int wait=backoff+jitter.Next(0,Math.Max(1,backoff));if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait*1000L;else{nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait*1000L;nextUploadAttemptAt=nextHeartbeatAt;}backoff=Math.Min(60,backoff*2);}
                catch(Exception e){CloudNetworkErrors.Write(e);lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;string description=CloudNetworkErrors.Describe(e);Update(description.StartsWith("协议错误",StringComparison.Ordinal)?"协议错误":"离线重试",description+"；本地记录继续工作",false);long wait=Math.Min(60,backoff+jitter.Next(0,Math.Max(1,backoff)));if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait*1000L;else{nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait*1000L;nextUploadAttemptAt=nextHeartbeatAt;}backoff=Math.Min(60,backoff*2);}
                await Delay(250);
            }
        }
        async Task Delay(int ms){try{await Task.Delay(ms,shutdown.Token).ConfigureAwait(false);}catch(OperationCanceledException){}}
        internal static bool IsFresh(int periodSeconds,double capturedAgeSeconds,double pendingElapsedSeconds){double maxAge=Math.Max(30,2.0*Math.Max(0,periodSeconds)+5);return capturedAgeSeconds+pendingElapsedSeconds<=maxAge;}
        void Update(string state,string detail,bool subscribed){bool changed;lock(sync){changed=status.State!=state||status.Detail!=detail||status.HasSubscription!=subscribed||status.Merged!=merged||notifiedSent!=status.Sent||notifiedFailed!=status.Failed||notifiedContact!=status.LastContactUtc||notifiedUpload!=status.LastUploadUtc;status.State=state;status.Detail=detail;status.HasSubscription=subscribed;status.Merged=merged;notifiedSent=status.Sent;notifiedFailed=status.Failed;notifiedContact=status.LastContactUtc;notifiedUpload=status.LastUploadUtc;}if(changed){EventHandler h=StatusChanged;if(h!=null)try{h(this,EventArgs.Empty);}catch{}}}
        static CloudRealtimeStatus Copy(CloudRealtimeStatus x){return new CloudRealtimeStatus{State=x.State,Detail=x.Detail,LastContactUtc=x.LastContactUtc,LastUploadUtc=x.LastUploadUtc,LastSampleUtc=x.LastSampleUtc,HasSubscription=x.HasSubscription,Sent=x.Sent,Failed=x.Failed,Merged=x.Merged};}
        public void Dispose(){lock(sync){if(disposed)return;disposed=true;}CancelBackfill();shutdown.Cancel();try{worker.GetAwaiter().GetResult();}catch{}Task pending;lock(backfillGate)pending=backfillTask;try{if(pending!=null)pending.GetAwaiter().GetResult();}catch{}shutdown.Dispose();}
        internal static int RunSelfTests()
        {
            int addedChecks=0;if(!IsFresh(60,32,0))throw new Exception("60-second sampling period rejected a 32-second sample");addedChecks++;if(IsFresh(2,32,0))throw new Exception("2-second sampling period accepted a 32-second sample");addedChecks++;if(IsFresh(2,29,2))throw new Exception("monotonic pending age was not added to captured sample age");addedChecks++;
            string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".cloud-test");if(Directory.Exists(root))Directory.Delete(root,true);Directory.CreateDirectory(root);try
            {
                CloudConfiguration persisted=new CloudConfiguration{Enabled=true,IncludeSimulation=false,DeviceId="device-test",Alias="lab",Endpoint="https://example.invalid",Token="token-must-not-appear",SavePath=Path.Combine(root,"cloud.txt")};persisted.Save();string raw=File.ReadAllText(persisted.SavePath);if(raw.Contains(persisted.Token))throw new Exception("cloud token stored as clear text");CloudConfiguration loaded=CloudConfiguration.Load(persisted.SavePath,persisted.DeviceId);if(!loaded.Enabled||loaded.Token!=persisted.Token||!loaded.HasValidEndpoint)throw new Exception("cloud DPAPI configuration roundtrip failed");loaded.Endpoint="http://127.0.0.1";if(loaded.HasValidEndpoint)throw new Exception("non-HTTPS cloud endpoint was accepted");
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
                return 9+addedChecks+CloudTransportTests.Run();
            }
            finally{string full=Path.GetFullPath(root),basePath=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);if(full.StartsWith(basePath,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(full))Directory.Delete(full,true);}
        }
        static RealtimeSnapshot TestSnapshot(string source,DateTime time,int pack=1){Frame f=Protocol.Decode(Simulator.Respond(1,0x42,(byte)pack));string layout;List<PackData> data=DataParser.Realtime(f.Info,(byte)pack,out layout);return RealtimeSnapshot.Capture(f,data[0],source,time,null,7,2);}
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
