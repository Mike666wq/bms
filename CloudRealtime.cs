using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
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
                if(map.TryGetValue("token",out value)&&value.Length>0)try{c.Token=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),null,DataProtectionScope.CurrentUser));}catch{c.Token="";}
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
    sealed class CloudAcknowledgement{[DataMember(Name="accepted")]public bool Accepted{get;set;}}
    public sealed class CloudHttpException:Exception
    {
        public int StatusCode{get;private set;}
        public CloudHttpException(int status,string message):base(message){StatusCode=status;}
    }
    public interface ICloudTransport
    {
        Task<CloudHeartbeatReply> HeartbeatAsync(CloudConfiguration configuration,CancellationToken token);
        Task SendSnapshotAsync(CloudConfiguration configuration,CloudSnapshotPost post,CancellationToken token);
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
        public string State{get;internal set;}public string Detail{get;internal set;}public DateTime? LastContactUtc{get;internal set;}public DateTime? LastUploadUtc{get;internal set;}public bool HasSubscription{get;internal set;}public long Sent{get;internal set;}public long Failed{get;internal set;}public long Merged{get;internal set;}
    }
    public sealed class CloudRealtimeService: IRealtimePublisher,IDisposable
    {
        sealed class Pending{public RealtimeSnapshot Snapshot;public long Sequence;public DateTime UpdatedUtc;}
        readonly object sync=new object();readonly Func<CloudConfiguration> configuration;readonly ICloudTransport transport;readonly CancellationTokenSource shutdown=new CancellationTokenSource();readonly Dictionary<string,Pending> latest=new Dictionary<string,Pending>(StringComparer.Ordinal);readonly Dictionary<string,long> sent=new Dictionary<string,long>(StringComparer.Ordinal);readonly string sessionId=Guid.NewGuid().ToString("N");readonly Task worker;readonly Random jitter=new Random();CloudRealtimeStatus status=new CloudRealtimeStatus{State="未启用",Detail="云端连接默认关闭"};long sequence,merged,notifiedSent,notifiedFailed;bool disposed;DateTime? notifiedContact,notifiedUpload;
        public event EventHandler StatusChanged;
        public CloudRealtimeStatus Status{get{lock(sync)return Copy(status);}}
        public CloudRealtimeService(Func<CloudConfiguration> getConfiguration,ICloudTransport cloudTransport=null){configuration=getConfiguration;transport=cloudTransport??new HttpsCloudTransport();worker=Task.Run((Func<Task>)RunAsync);}
        public void Publish(RealtimeSnapshot snapshot)
        {
            if(snapshot==null||snapshot.Pack<1||snapshot.Pack>16)return;lock(sync){if(disposed)return;CloudConfiguration c;try{c=configuration();}catch{return;}if(c==null||!c.Enabled||(snapshot.Source=="simulation"&&!c.IncludeSimulation))return;string key=snapshot.Source+":"+snapshot.Pack;Pending old;if(latest.TryGetValue(key,out old))merged++;else if(latest.Count>=16){string drop=null;DateTime oldest=DateTime.MaxValue;foreach(KeyValuePair<string,Pending> pair in latest)if(pair.Value.UpdatedUtc<oldest){oldest=pair.Value.UpdatedUtc;drop=pair.Key;}if(drop!=null){latest.Remove(drop);sent.Remove(drop);merged++;}}latest[key]=new Pending{Snapshot=snapshot,Sequence=++sequence,UpdatedUtc=DateTime.UtcNow};status.Merged=merged;} }
        async Task RunAsync()
        {
            Stopwatch scheduler=Stopwatch.StartNew();long nextHeartbeatAt=0,nextUploadAttemptAt=0;int backoff=1;bool uploadRequest=false,uploadHasFailed=false;Stopwatch lease=Stopwatch.StartNew();long leaseDeadline=0;string subscription=null;HashSet<int> requested=new HashSet<int>();CloudConfiguration activeConfiguration=null;
            while(!shutdown.IsCancellationRequested)
            {
                CloudConfiguration c=null;try{c=configuration();}catch{}if(c==null||!c.Enabled){subscription=null;leaseDeadline=0;activeConfiguration=null;lock(sync){latest.Clear();sent.Clear();}Update("未启用","云端连接默认关闭",false);await Delay(1000);continue;}if(!ReferenceEquals(activeConfiguration,c)){activeConfiguration=c;subscription=null;leaseDeadline=0;nextHeartbeatAt=0;nextUploadAttemptAt=0;requested.Clear();}if(!c.HasValidEndpoint||String.IsNullOrWhiteSpace(c.Token)){Update("等待配置","请输入 HTTPS 服务地址和设备令牌",false);await Delay(1000);continue;}
                try
                {
                    if(scheduler.ElapsedMilliseconds>=nextHeartbeatAt){uploadRequest=false;CloudHeartbeatReply reply=await transport.HeartbeatAsync(c,shutdown.Token).ConfigureAwait(false);if(reply==null||reply.LeaseSeconds<0||reply.LeaseSeconds>3600||reply.RequestedPacks==null||reply.RequestedPacks.Length>16||(reply.LeaseSeconds>0&&String.IsNullOrWhiteSpace(reply.SubscriptionId)))throw new InvalidDataException("心跳租约响应字段无效");int[] packs=reply.RequestedPacks;foreach(int pack in packs)if(pack<1||pack>16)throw new InvalidDataException("心跳租约包含无效 Pack");lock(sync){status.LastContactUtc=DateTime.UtcNow;}nextHeartbeatAt=scheduler.ElapsedMilliseconds+(reply.LeaseSeconds>0?Math.Min(15000,Math.Max(2000,reply.LeaseSeconds*500)):15000);if(!uploadHasFailed)backoff=1;subscription=reply.SubscriptionId;requested=new HashSet<int>(packs);if(!String.IsNullOrEmpty(subscription)&&reply.LeaseSeconds>0){lease.Restart();leaseDeadline=(long)reply.LeaseSeconds*1000;Update("在线等待观看","收到观看租约",true);}else{leaseDeadline=0;Update("在线等待观看","暂无观看订阅，仅发送心跳",false);}}
                    if(leaseDeadline>0&&lease.ElapsedMilliseconds<leaseDeadline&&subscription!=null){KeyValuePair<string,Pending>? candidate=null;if(scheduler.ElapsedMilliseconds>=nextUploadAttemptAt)lock(sync){foreach(KeyValuePair<string,Pending> pair in latest){Pending p=pair.Value;if(p.Snapshot.ReceivedUtc<DateTime.UtcNow.AddSeconds(-30)||!requested.Contains(p.Snapshot.Pack))continue;long sentSeq;if(!sent.TryGetValue(pair.Key,out sentSeq)||sentSeq!=p.Sequence){candidate=pair;break;}}}if(candidate.HasValue){uploadRequest=true;Pending p=candidate.Value.Value;CloudSnapshotEnvelope envelope=CloudSnapshotEnvelope.From(p.Snapshot,c.DeviceId,sessionId,p.Sequence);await transport.SendSnapshotAsync(c,new CloudSnapshotPost{SubscriptionId=subscription,Snapshot=envelope},shutdown.Token).ConfigureAwait(false);uploadRequest=false;uploadHasFailed=false;lock(sync){sent[candidate.Value.Key]=p.Sequence;status.Sent++;status.LastUploadUtc=p.Snapshot.ReceivedUtc;}Update("上传中","最近快照已确认",true);backoff=1;nextUploadAttemptAt=0;continue;}Update("在线等待观看",scheduler.ElapsedMilliseconds<nextUploadAttemptAt?"上传失败，等待重试":"租约有效；没有可上传的新鲜快照",true);}else if(leaseDeadline>0){leaseDeadline=0;Update("在线等待观看","观看租约已过期，上传已暂停",false);}
                }
                catch(OperationCanceledException){if(shutdown.IsCancellationRequested)break;lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;Update("离线重试","云端请求已取消；本地记录继续工作",false);long wait=1000L*backoff;if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait;else nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait;backoff=Math.Min(60,backoff*2);}
                catch(CloudHttpException e){lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;if(e.StatusCode==401||e.StatusCode==403){leaseDeadline=0;subscription=null;Update("认证失败","令牌被拒绝；请更新配置",false);nextHeartbeatAt=scheduler.ElapsedMilliseconds+60000;nextUploadAttemptAt=nextHeartbeatAt;}else{Update("离线重试","网络请求失败；本地记录继续工作",false);int wait=backoff+jitter.Next(0,Math.Max(1,backoff));if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait*1000L;else{nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait*1000L;nextUploadAttemptAt=nextHeartbeatAt;}backoff=Math.Min(60,backoff*2);}}
                catch(TimeoutException){lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;Update("离线重试","云端请求超时；本地记录继续工作",false);long wait=(backoff+jitter.Next(0,Math.Max(1,backoff)))*1000L;if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait;else{nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait;nextUploadAttemptAt=nextHeartbeatAt;}backoff=Math.Min(60,backoff*2);}
                catch(WebException){lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;Update("离线重试","网络请求失败；本地记录继续工作",false);int wait=backoff+jitter.Next(0,Math.Max(1,backoff));if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait*1000L;else{nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait*1000L;nextUploadAttemptAt=nextHeartbeatAt;}backoff=Math.Min(60,backoff*2);}
                catch(Exception e){lock(sync){status.Failed++;}if(uploadRequest)uploadHasFailed=true;Update("协议错误",SafeMessage(e),false);long wait=Math.Min(60,backoff+jitter.Next(0,Math.Max(1,backoff)));if(uploadRequest)nextUploadAttemptAt=scheduler.ElapsedMilliseconds+wait*1000L;else{nextHeartbeatAt=scheduler.ElapsedMilliseconds+wait*1000L;nextUploadAttemptAt=nextHeartbeatAt;}backoff=Math.Min(60,backoff*2);}
                await Delay(250);
            }
        }
        async Task Delay(int ms){try{await Task.Delay(ms,shutdown.Token).ConfigureAwait(false);}catch(OperationCanceledException){}}
        static string SafeMessage(Exception e){return e is InvalidDataException?e.Message:"网络或协议请求失败";}
        void Update(string state,string detail,bool subscribed){bool changed;lock(sync){changed=status.State!=state||status.Detail!=detail||status.HasSubscription!=subscribed||status.Merged!=merged||notifiedSent!=status.Sent||notifiedFailed!=status.Failed||notifiedContact!=status.LastContactUtc||notifiedUpload!=status.LastUploadUtc;status.State=state;status.Detail=detail;status.HasSubscription=subscribed;status.Merged=merged;notifiedSent=status.Sent;notifiedFailed=status.Failed;notifiedContact=status.LastContactUtc;notifiedUpload=status.LastUploadUtc;}if(changed){EventHandler h=StatusChanged;if(h!=null)try{h(this,EventArgs.Empty);}catch{}}}
        static CloudRealtimeStatus Copy(CloudRealtimeStatus x){return new CloudRealtimeStatus{State=x.State,Detail=x.Detail,LastContactUtc=x.LastContactUtc,LastUploadUtc=x.LastUploadUtc,HasSubscription=x.HasSubscription,Sent=x.Sent,Failed=x.Failed,Merged=x.Merged};}
        public void Dispose(){lock(sync){if(disposed)return;disposed=true;}shutdown.Cancel();try{worker.GetAwaiter().GetResult();}catch{}shutdown.Dispose();}
        internal static int RunSelfTests()
        {
            string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".cloud-test");if(Directory.Exists(root))Directory.Delete(root,true);Directory.CreateDirectory(root);try
            {
                CloudConfiguration persisted=new CloudConfiguration{Enabled=true,IncludeSimulation=false,DeviceId="device-test",Alias="lab",Endpoint="https://example.invalid",Token="token-must-not-appear",SavePath=Path.Combine(root,"cloud.txt")};persisted.Save();string raw=File.ReadAllText(persisted.SavePath);if(raw.Contains(persisted.Token))throw new Exception("cloud token stored as clear text");CloudConfiguration loaded=CloudConfiguration.Load(persisted.SavePath,persisted.DeviceId);if(!loaded.Enabled||loaded.Token!=persisted.Token||!loaded.HasValidEndpoint)throw new Exception("cloud DPAPI configuration roundtrip failed");loaded.Endpoint="http://127.0.0.1";if(loaded.HasValidEndpoint)throw new Exception("non-HTTPS cloud endpoint was accepted");
                FakeCloudTransport fake=new FakeCloudTransport();CloudConfiguration runtime=new CloudConfiguration{Enabled=true,DeviceId="device-test",Alias="lab",Endpoint="https://example.invalid",Token="fake-only"};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},fake))
                {
                    RealtimeSnapshot snapshot=TestSnapshot("serial",DateTime.UtcNow);service.Publish(snapshot);DateTime limit=DateTime.UtcNow.AddSeconds(4);while(fake.Uploads<1&&DateTime.UtcNow<limit)Thread.Sleep(25);if(fake.Uploads!=1)throw new Exception("fake lease did not deliver latest snapshot");CloudSnapshotPost sent=fake.Last;if(sent==null||sent.SubscriptionId!="fake-subscription"||sent.Snapshot.DeviceId!="device-test"||sent.Snapshot.Sequence!=1||sent.Snapshot.Source!="serial")throw new Exception("uploaded snapshot contract fields failed");string missingAlert=SerializeEnvelope(sent.Snapshot);if(!missingAlert.Contains("\"alarmObservationAvailable\":false")||!missingAlert.Contains("\"alarmObservation\":null")||!missingAlert.Contains("\"periodSeconds\":2")||!missingAlert.Contains("\"capturedUtc\":\"" )||!missingAlert.Contains("Z\""))throw new Exception("cloud snapshot omitted UTC interval or explicit missing-alert status");Frame af=Protocol.Decode(Simulator.Respond(1,0x44,1));AlarmSnapshot alarm=AlarmSnapshot.Capture(af,"serial",1,DateTime.UtcNow.AddMinutes(-1),"test alarm",6,2);string withAlert=SerializeEnvelope(CloudSnapshotEnvelope.From(snapshot,"device-test","session",2,alarm));if(!withAlert.Contains("\"observedUtc\":\"")||!withAlert.Contains("\"acquisitionRound\":6")||!withAlert.Contains("\"alarmObservationAvailable\":true")||!withAlert.Contains("Z\""))throw new Exception("cloud alert did not preserve its independent UTC observation metadata");
                    Thread.Sleep(1200);service.Publish(TestSnapshot("serial",DateTime.UtcNow));Thread.Sleep(500);if(fake.Uploads!=1)throw new Exception("expired fake lease continued uploading");if(service.Status.HasSubscription)throw new Exception("expired lease remained active");
                }
                runtime.IncludeSimulation=true;FakeCloudTransport noLease=new FakeCloudTransport{LeaseSeconds=0};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},noLease)){int events=0;service.StatusChanged+=delegate{Interlocked.Increment(ref events);};for(int p=1;p<=16;p++)service.Publish(TestSnapshot("serial",DateTime.UtcNow,p));service.Publish(TestSnapshot("simulation",DateTime.UtcNow,1));Thread.Sleep(600);if(noLease.Uploads!=0)throw new Exception("snapshot uploaded without a viewing lease");if(service.Status.Merged<1)throw new Exception("latest-only cache did not enforce its 16-pack capacity");int stableEvents=events;Thread.Sleep(700);if(events!=stableEvents)throw new Exception("unchanged cloud status repeatedly raised UI notifications");}
                FakeCloudTransport retry=new FakeCloudTransport{LeaseSeconds=10,FailFirst=true};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},retry)){service.Publish(TestSnapshot("serial",DateTime.UtcNow));DateTime firstLimit=DateTime.UtcNow.AddSeconds(2);while(retry.Attempts<1&&DateTime.UtcNow<firstLimit)Thread.Sleep(25);Thread.Sleep(500);if(retry.Attempts!=1)throw new Exception("failed snapshot was retried faster than its backoff");DateTime retryLimit=DateTime.UtcNow.AddSeconds(5);while(retry.Attempts<2&&DateTime.UtcNow<retryLimit)Thread.Sleep(25);if(retry.Attempts<2||retry.Uploads!=1||retry.FirstSequence!=retry.LastSequence)throw new Exception("snapshot retry changed its id/sequence or failed to recover");}
                FakeCloudTransport malformed=new FakeCloudTransport{Reply=new CloudHeartbeatReply{SubscriptionId="bad",LeaseSeconds=30,RequestedPacks=new[]{17}}};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},malformed)){DateTime until=DateTime.UtcNow.AddSeconds(2);while(service.Status.State!="协议错误"&&DateTime.UtcNow<until)Thread.Sleep(25);if(service.Status.State!="协议错误")throw new Exception("invalid lease response was not rejected");}
                FakeCloudTransport unauthorized=new FakeCloudTransport{HeartbeatFailure=new CloudHttpException(401,"fake unauthorized")};using(CloudRealtimeService service=new CloudRealtimeService(delegate{return runtime;},unauthorized)){DateTime until=DateTime.UtcNow.AddSeconds(2);while(service.Status.State!="认证失败"&&DateTime.UtcNow<until)Thread.Sleep(25);if(service.Status.State!="认证失败")throw new Exception("401 did not produce authentication failure state");}
                return 9+CloudTransportTests.Run();
            }
            finally{string full=Path.GetFullPath(root),basePath=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);if(full.StartsWith(basePath,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(full))Directory.Delete(full,true);}
        }
        static RealtimeSnapshot TestSnapshot(string source,DateTime time,int pack=1){Frame f=Protocol.Decode(Simulator.Respond(1,0x42,(byte)pack));string layout;List<PackData> data=DataParser.Realtime(f.Info,(byte)pack,out layout);return RealtimeSnapshot.Capture(f,data[0],source,time,null,7,2);}
        static string SerializeEnvelope(CloudSnapshotEnvelope envelope){using(MemoryStream ms=new MemoryStream()){new DataContractJsonSerializer(typeof(CloudSnapshotEnvelope)).WriteObject(ms,envelope);return Encoding.UTF8.GetString(ms.ToArray());}}
        sealed class FakeCloudTransport:ICloudTransport
        {
            public int Uploads,Attempts;public int LeaseSeconds=1;public bool FailFirst;public long FirstSequence,LastSequence;public CloudSnapshotPost Last;public CloudHeartbeatReply Reply;public Exception HeartbeatFailure;
            public Task<CloudHeartbeatReply> HeartbeatAsync(CloudConfiguration c,CancellationToken t){if(HeartbeatFailure!=null){TaskCompletionSource<CloudHeartbeatReply> failed=new TaskCompletionSource<CloudHeartbeatReply>();failed.SetException(HeartbeatFailure);return failed.Task;}return Task.FromResult(Reply??new CloudHeartbeatReply{SubscriptionId=LeaseSeconds>0?"fake-subscription":"",LeaseSeconds=LeaseSeconds,RequestedPacks=new[]{1}});}
            public Task SendSnapshotAsync(CloudConfiguration c,CloudSnapshotPost post,CancellationToken t){Last=post;if(Interlocked.Increment(ref Attempts)==1)FirstSequence=post.Snapshot.Sequence;LastSequence=post.Snapshot.Sequence;if(FailFirst&&Attempts==1){TaskCompletionSource<object> failed=new TaskCompletionSource<object>();failed.SetException(new IOException("fake temporary failure"));return failed.Task;}Interlocked.Increment(ref Uploads);return Task.FromResult(0);}
        }
    }
}
