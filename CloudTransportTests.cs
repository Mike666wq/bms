using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BmsSerialDemo
{
    internal static class CloudTransportTests
    {
        static int checks;
        static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("云端真实HTTP自测失败：" + message); }
        static HttpsCloudTransport Transport(ControlledCloudTransportTestHost host, int timeout = 2000)
        { return new HttpsCloudTransport(delegate(Uri uri) { return (HttpWebRequest)WebRequest.Create(new Uri(host.Endpoint.TrimEnd('/') + uri.AbsolutePath)); }, timeout, true); }
        static CloudConfiguration Config(ControlledCloudTransportTestHost host, string token = "only-test-secret")
        { return new CloudConfiguration { Enabled = true, DeviceId = "test-device", Alias = "controlled", Endpoint = host.Endpoint, Token = token }; }
        public static int Run()
        {
            checks = 0;
            using (ControlledCloudTransportTestHost host = new ControlledCloudTransportTestHost())
            {
                HttpsCloudTransport transport = Transport(host); CloudConfiguration config = Config(host);
                CloudHeartbeatReply reply = transport.HeartbeatAsync(config, CancellationToken.None).GetAwaiter().GetResult();
                Check(reply.SubscriptionId == "test-sub" && host.LastPath == "/api/realtime/heartbeat", "heartbeat使用正确路由并解析JSON");
                Check(host.LastAuthorization == "Bearer only-test-secret", "Bearer令牌仅存在于授权头");
                Check(host.LastBody.Contains("\"deviceId\":\"test-device\"") && !host.LastBody.Contains(config.Token), "心跳JSON含设备身份且不含令牌");

                Frame frame = new Frame { Address = 1, Raw = new byte[0], Info = new byte[0] };
                PackData parsed = new PackData { Pack = 1, Soc = 80, Soh = 100, Voltage = 52.4, Current = 1.2, Cells = new int[16], Temperatures = new int[4] };
                RealtimeSnapshot sample = RealtimeSnapshot.Capture(frame, parsed, "serial", DateTime.UtcNow, null, 4, 2);
                CloudSnapshotPost post = new CloudSnapshotPost { SubscriptionId = "test-sub", Snapshot = CloudSnapshotEnvelope.From(sample, config.DeviceId, "test-session", 8) };
                host.SetReply(new ControlledCloudTransportTestHost.Reply { Body = "{\"accepted\":true}" });
                transport.SendSnapshotAsync(config, post, CancellationToken.None).GetAwaiter().GetResult();
                Check(host.LastPath == "/api/realtime/snapshots" && host.LastBody.Contains("\"sequence\":8") && host.LastBody.Contains("\"subscriptionId\":\"test-sub\""), "快照路由/JSON字段及确认解析");
                host.SetReply(new ControlledCloudTransportTestHost.Reply { Body = "{\"accepted\":false}" });
                try { transport.SendSnapshotAsync(config, post, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("expected rejected acknowledgement"); }
                catch (InvalidDataException) { Check(true, "拒绝的快照确认不计成功"); }
                host.SetReply(new ControlledCloudTransportTestHost.Reply());

                foreach (int code in new[] { 401, 403, 500 })
                {
                    host.SetReply(new ControlledCloudTransportTestHost.Reply { Status = code, Body = "sensitive-error-body" });
                    try { transport.HeartbeatAsync(config, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("expected HTTP status failure"); }
                    catch (CloudHttpException e) { Check(e.StatusCode == code, "HTTP状态码分类 " + code); Check(!e.Message.Contains("sensitive-error-body") && !e.Message.Contains(config.Token), "HTTP异常不泄露响应内容或令牌"); }
                }
                host.SetReply(new ControlledCloudTransportTestHost.Reply { Body = "not-json" }); bool malformedRejected = false;
                try { transport.HeartbeatAsync(config, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (System.Runtime.Serialization.SerializationException) { malformedRejected = true; }
                catch (InvalidDataException) { malformedRejected = true; }
                Check(malformedRejected, "malformed JSON明确失败");
                host.SetReply(new ControlledCloudTransportTestHost.Reply { Body = "{\"subscriptionId\":null}" }); bool missingFieldsRejected = false;
                try { transport.HeartbeatAsync(config, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (System.Runtime.Serialization.SerializationException) { missingFieldsRejected = true; }
                Check(missingFieldsRejected, "缺失租约必需字段的JSON被拒绝");

                host.SetReply(new ControlledCloudTransportTestHost.Reply { Body = new string('x', 65537) });
                try { transport.HeartbeatAsync(config, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("expected content-length cap"); }
                catch (InvalidDataException e) { Check(e.Message.Contains("64 KiB"), "Content-Length响应上限"); }
                host.SetReply(new ControlledCloudTransportTestHost.Reply { Body = new string('x', 65537), Chunked = true });
                try { transport.HeartbeatAsync(config, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("expected chunked cap"); }
                catch (InvalidDataException e) { Check(e.Message.Contains("64 KiB"), "chunked响应实际读取上限"); }

                using (ControlledCloudTransportTestHost redirected = new ControlledCloudTransportTestHost())
                {
                    host.SetReply(new ControlledCloudTransportTestHost.Reply { Status = 302, Redirect = redirected.Endpoint });
                    try { transport.HeartbeatAsync(config, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("expected redirect rejection"); }
                    catch (CloudHttpException e) { Check(e.StatusCode == 302 && redirected.Requests == 0, "302拒绝且不访问跳转目标"); Check(host.LastAuthorization == "Bearer " + config.Token, "跳转请求未重放到第二主机"); }
                }

                Stopwatch watch = Stopwatch.StartNew();
                using (ControlledCloudTransportTestHost headerHost = new ControlledCloudTransportTestHost())
                { headerHost.SetReply(new ControlledCloudTransportTestHost.Reply { DelayBeforeHeaders = 700 }); bool headerTimedOut = false; try { Transport(headerHost, 250).HeartbeatAsync(Config(headerHost), CancellationToken.None).GetAwaiter().GetResult(); } catch (TimeoutException) { headerTimedOut = true; } Check(headerTimedOut && watch.ElapsedMilliseconds < 1500, "等待响应头受整体时限约束"); }
                watch.Restart(); using (ControlledCloudTransportTestHost bodyHost = new ControlledCloudTransportTestHost())
                { bodyHost.SetReply(new ControlledCloudTransportTestHost.Reply { DelayBeforeBody = 700 }); bool bodyTimedOut = false; try { Transport(bodyHost, 250).HeartbeatAsync(Config(bodyHost), CancellationToken.None).GetAwaiter().GetResult(); } catch (TimeoutException) { bodyTimedOut = true; } Check(bodyTimedOut && watch.ElapsedMilliseconds < 1500, "读取响应体受整体时限约束"); }

                using (ControlledCloudTransportTestHost cancelHost = new ControlledCloudTransportTestHost())
                { cancelHost.SetReply(new ControlledCloudTransportTestHost.Reply { DelayBeforeHeaders = 700 }); using (CancellationTokenSource cancel = new CancellationTokenSource(100))
                    { watch.Restart(); bool cancelled = false; try { Transport(cancelHost).HeartbeatAsync(Config(cancelHost), cancel.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; } Check(cancelled && watch.ElapsedMilliseconds < 1500, "调用方取消可Abort挂起HTTP请求"); } }

                using (ControlledCloudTransportTestHost recoveryHost = new ControlledCloudTransportTestHost())
                {
                    recoveryHost.SetReply(new ControlledCloudTransportTestHost.Reply { DelayBeforeHeaders = 1800 });
                    CloudConfiguration secureConfig = new CloudConfiguration { Enabled = true, DeviceId = "recovery-test", Endpoint = "https://example.invalid", Token = config.Token };
                    HttpsCloudTransport recoveryTransport = new HttpsCloudTransport(delegate(Uri uri) { return (HttpWebRequest)WebRequest.Create(new Uri(recoveryHost.Endpoint.TrimEnd('/') + uri.AbsolutePath)); }, 1000, false);
                    using (CloudRealtimeService recovering = new CloudRealtimeService(delegate { return secureConfig; }, recoveryTransport))
                    {
                        DateTime started = DateTime.UtcNow.AddSeconds(2); while (recoveryHost.Requests == 0 && DateTime.UtcNow < started) Thread.Sleep(10); recoveryHost.SetReply(new ControlledCloudTransportTestHost.Reply()); DateTime limit = DateTime.UtcNow.AddSeconds(5);
                        while ((!recovering.Status.LastContactUtc.HasValue || recovering.Status.Failed == 0) && DateTime.UtcNow < limit) Thread.Sleep(25);
                        CloudRealtimeStatus recovered = recovering.Status; Check(recovered.Failed > 0 && recovered.LastContactUtc.HasValue, "真实HTTP心跳超时后worker按退避恢复 (requests=" + recoveryHost.Requests + ", failed=" + recovered.Failed + ", contact=" + recovered.LastContactUtc.HasValue + ", state=" + recovered.State + ", detail=" + recovered.Detail + ")");
                    }
                }

                using (ControlledCloudTransportTestHost disposeHost = new ControlledCloudTransportTestHost())
                {
                    disposeHost.SetReply(new ControlledCloudTransportTestHost.Reply { DelayBeforeHeaders = 1500 });
                    CloudConfiguration secureConfig = new CloudConfiguration { Enabled = true, DeviceId = "dispose-test", Endpoint = "https://example.invalid", Token = config.Token };
                    HttpsCloudTransport mappedTransport = new HttpsCloudTransport(delegate(Uri uri) { return (HttpWebRequest)WebRequest.Create(new Uri(disposeHost.Endpoint.TrimEnd('/') + uri.AbsolutePath)); }, 5000, false);
                    CloudRealtimeService service = new CloudRealtimeService(delegate { return secureConfig; }, mappedTransport); DateTime requestLimit = DateTime.UtcNow.AddSeconds(2); while (disposeHost.Requests == 0 && DateTime.UtcNow < requestLimit) Thread.Sleep(10);
                    bool requestWasActive = disposeHost.Requests > 0; watch.Restart(); service.Dispose(); Check(requestWasActive && watch.ElapsedMilliseconds < 1200, "Dispose取消真实挂起请求并等待worker退出");
                }
            }
            return checks;
        }
    }
}
