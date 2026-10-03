using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BmsSerialDemo
{
    /// <summary>Privacy-safe, bounded diagnostics for the configured cloud endpoint.</summary>
    internal static class CloudDiagnostics
    {
        const int StepTimeoutMilliseconds = 5000;

        internal static async Task<string> RunAsync(CloudConfiguration c, CancellationToken ct)
        {
            StringBuilder report = new StringBuilder();
            try
            {
                Add(report, "系统", "PASS", "Windows " + ReadWindowsVersion() + " (" + Environment.OSVersion.Version + "); .NET " + Environment.Version + "，Release " + ReadDotNetRelease() + "; 进程 " + (Environment.Is64BitProcess ? "64位" : "32位") + "; SecurityProtocol=" + ServicePointManager.SecurityProtocol + "; 应用 " + Assembly.GetExecutingAssembly().GetName().Version + "; 本地时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + "; 时区 " + TimeZoneInfo.Local.DisplayName + "; Token " + (c != null && !String.IsNullOrWhiteSpace(c.Token) ? "已配置" : "缺失"));
                Uri endpoint = null;
                bool valid = c != null && c.HasValidEndpoint && Uri.TryCreate(c.Endpoint, UriKind.Absolute, out endpoint);
                Add(report, "HTTPS配置", valid ? "PASS" : "FAIL", valid ? "地址格式有效（HTTPS、无查询参数和用户信息）；主机 " + endpoint.DnsSafeHost + ":" + endpoint.Port : "需要有效的 HTTPS 服务地址");
                Add(report, ".NET 4.x", ReadDotNetReleaseNumber() >= 528040 ? "PASS" : "FAIL", "NDP/v4/Full Release（64位视图）=" + ReadDotNetRelease());
                if(c!=null&&c.SavedTokenUnreadable)Add(report,"设备令牌","FAIL","已保存令牌无法由当前 Windows 用户解密；跨电脑复制的 DPAPI 配置需在本机重新输入令牌");
                if(c!=null&&!c.Enabled)Add(report,"连接开关","SKIP","已保存的云端连接开关未启用；本次诊断仍可单独检查连接");
                if (!valid)
                {
                    Add(report, "代理", "SKIP", "HTTPS 地址无效");
                    Add(report, "DNS", "SKIP", "HTTPS 地址无效");
                    Add(report, "直连 TCP", "SKIP", "HTTPS 地址无效");
                    Add(report, "直连 TLS 1.2", "SKIP", "HTTPS 地址无效");
                    Add(report, "生产心跳", "SKIP", "HTTPS 地址无效");
                    return report.ToString();
                }

                ct.ThrowIfCancellationRequested();
                string proxy = await Bounded("代理", token => Task.Run(() => DescribeProxy(endpoint)), ct).ConfigureAwait(false);
                Add(report, "默认代理", proxy == null || proxy.StartsWith("FAIL|", StringComparison.Ordinal) ? "FAIL" : "PASS", proxy == null ? "读取默认代理超时" : proxy.StartsWith("FAIL|", StringComparison.Ordinal) ? proxy.Substring(5) : proxy);
                ct.ThrowIfCancellationRequested();

                IPAddress[] addresses = null;
                string dns = await Bounded("DNS", async token =>
                {
                    IPAddress[] found = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost).ConfigureAwait(false);
                    addresses = found;
                    return found.Length == 0 ? "FAIL|未返回地址" : String.Join(", ", Array.ConvertAll(found, a => a.ToString()));
                }, ct).ConfigureAwait(false);
                Add(report, "DNS", dns == null || dns.StartsWith("FAIL|", StringComparison.Ordinal) ? "FAIL" : "PASS", dns == null ? "解析超时" : dns.StartsWith("FAIL|", StringComparison.Ordinal) ? dns.Substring(5) : dns);
                ct.ThrowIfCancellationRequested();

                string tcp = await Bounded("TCP", token => ProbeTcpAsync(endpoint, addresses, token), ct).ConfigureAwait(false);
                Add(report, "直连 TCP", tcp == null || tcp.StartsWith("FAIL|", StringComparison.Ordinal) ? "FAIL" : "PASS", tcp == null ? "连接超时" : tcp.StartsWith("FAIL|", StringComparison.Ordinal) ? tcp.Substring(5) + "；直连失败时，代理路径仍可能正常" : tcp);
                ct.ThrowIfCancellationRequested();

                string tls = await Bounded("TLS", token => ProbeTlsAsync(endpoint, addresses, token), ct).ConfigureAwait(false);
                bool tlsOk = tls != null && !tls.StartsWith("FAIL|", StringComparison.Ordinal);
                Add(report, "直连 TLS 1.2", tlsOk ? "PASS" : "FAIL", tls == null ? "握手超时" : tlsOk ? tls : tls.Substring(5) + "；直连失败时，代理路径仍可能正常");
                ct.ThrowIfCancellationRequested();

                if (c == null || String.IsNullOrWhiteSpace(c.Token))
                    Add(report, "生产心跳", "SKIP", "Token 缺失，仅完成网络探测");
                else
                {
                    string heartbeat = await Bounded("心跳", async token =>
                    {
                        try
                        {
                            CloudHeartbeatReply reply = await new HttpsCloudTransport().HeartbeatAsync(c, token).ConfigureAwait(false);
                            if (!IsValidLease(reply)) return "FAIL|协议校验失败：租约字段无效";
                            if (reply.LeaseSeconds == 0 || String.IsNullOrWhiteSpace(reply.SubscriptionId)) return "PASS|心跳成功；当前没有观看租约（不代表连接失败）";
                            return "PASS|心跳成功；收到有效观看租约";
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { return "FAIL|" + CloudNetworkErrors.Describe(e); }
                    }, ct).ConfigureAwait(false);
                    if (heartbeat != null && heartbeat.StartsWith("PASS|", StringComparison.Ordinal)) Add(report, "生产心跳", "PASS", heartbeat.Substring(5));
                    else Add(report, "生产心跳", "FAIL", heartbeat != null && heartbeat.StartsWith("FAIL|", StringComparison.Ordinal) ? heartbeat.Substring(5) : "请求失败或超过5秒：" + CloudNetworkErrors.Describe(new TimeoutException()));
                }
            }
            catch (OperationCanceledException) { Add(report, "诊断", "SKIP", "已取消"); }
            catch (Exception e) { Add(report, "诊断", "FAIL", CloudNetworkErrors.Describe(e)); }
            return report.ToString();
        }

        internal static async Task<string> RunEndpointProbeAsync(Uri endpoint, CancellationToken ct)
        {
            if (endpoint == null || endpoint.Scheme != Uri.UriSchemeHttps || !String.IsNullOrEmpty(endpoint.UserInfo) || !String.IsNullOrEmpty(endpoint.Query) || !String.IsNullOrEmpty(endpoint.Fragment))
                return "FAIL HTTPS配置：仅允许无凭据、无查询参数的 HTTPS 地址";
            try
            {
                string result = await Bounded("TLS", token => ProbeTlsAsync(endpoint, null, token), ct).ConfigureAwait(false);
                return result == null ? "FAIL 直连 TLS 1.2：握手超时" : result.StartsWith("FAIL|", StringComparison.Ordinal) ? "FAIL 直连 TLS 1.2：" + result.Substring(5) + "；直连失败时，代理路径仍可能正常" : "PASS 直连 TLS 1.2：" + result;
            }
            catch (OperationCanceledException) { return "SKIP 诊断：已取消"; }
        }

        internal static bool IsValidLease(CloudHeartbeatReply reply)
        {
            if (reply == null || reply.LeaseSeconds < 0 || reply.LeaseSeconds > 3600 || reply.RequestedPacks == null || reply.RequestedPacks.Length > 16 || (reply.LeaseSeconds > 0 && String.IsNullOrWhiteSpace(reply.SubscriptionId))) return false;
            foreach (int pack in reply.RequestedPacks) if (pack < 1 || pack > 16) return false;
            return true;
        }

        static async Task<string> Bounded(string name, Func<CancellationToken, Task<string>> operation, CancellationToken caller)
        {
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(caller))
            {
                // Proxy discovery and APM networking can block before returning a Task.
                // Start each complete operation off the caller so the deadline and UI remain responsive.
                CancellationToken stepToken=linked.Token;
                Task<string> work=Task.Run(async delegate { stepToken.ThrowIfCancellationRequested();return await operation(stepToken).ConfigureAwait(false); },stepToken);
                Task delay = Task.Delay(StepTimeoutMilliseconds, caller);
                Task winner = await Task.WhenAny(work, delay).ConfigureAwait(false);
                if (winner == work)
                {
                    try { return await work.ConfigureAwait(false); } catch (OperationCanceledException) { linked.Cancel(); Observe(work); throw; } catch (Exception e) { linked.Cancel(); Observe(work); if (caller.IsCancellationRequested) throw new OperationCanceledException(caller); return "FAIL|" + CloudNetworkErrors.Describe(e); }
                }
                linked.Cancel();
                Observe(work);
                caller.ThrowIfCancellationRequested();
                return null;
            }
        }

        static void Observe(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        static async Task<string> ProbeTcpAsync(Uri endpoint, IPAddress[] addresses, CancellationToken ct)
        {
            IPAddress[] list = addresses;
            if (list == null || list.Length == 0) list = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost).ConfigureAwait(false);
            Exception last = null;
            foreach (IPAddress address in list)
            {
                ct.ThrowIfCancellationRequested();
                using (TcpClient client = new TcpClient(address.AddressFamily))
                {
                    try { using (ct.Register(() => { try { client.Close(); } catch { } })) await client.ConnectAsync(address, endpoint.Port).ConfigureAwait(false); return "已连接 " + address + ":" + endpoint.Port; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { last = e; }
                }
            }
            if (last != null) throw last;
            throw new SocketException();
        }

        static async Task<string> ProbeTlsAsync(Uri endpoint, IPAddress[] addresses, CancellationToken ct)
        {
            IPAddress[] list = addresses;
            if (list == null || list.Length == 0) list = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost).ConfigureAwait(false);
            Exception last = null;
            foreach (IPAddress address in list)
            {
                ct.ThrowIfCancellationRequested();
                using (TcpClient client = new TcpClient(address.AddressFamily))
                {
                    string policy = "未回调", chain = "未提供";
                    try
                    {
                        using (ct.Register(() => { try { client.Close(); } catch { } }))
                        {
                            await client.ConnectAsync(address, endpoint.Port).ConfigureAwait(false);
                            using (SslStream ssl = new SslStream(client.GetStream(), false, (sender, certificate, certChain, errors) =>
                            {
                                policy = errors.ToString();
                                if (certChain != null)
                                {
                                    List<string> statuses = new List<string>();
                                    foreach (X509ChainStatus status in certChain.ChainStatus) statuses.Add(status.Status.ToString());
                                    chain = statuses.Count == 0 ? "无" : String.Join(",", statuses.ToArray());
                                }
                                return errors == SslPolicyErrors.None;
                            }))
                            {
                                await ssl.AuthenticateAsClientAsync(endpoint.DnsSafeHost, null, SslProtocols.Tls12, true).ConfigureAwait(false);
                                X509Certificate2 cert = new X509Certificate2(ssl.RemoteCertificate);
                                try { return "证书验证通过；PolicyErrors=" + policy + "；ChainStatus=" + chain + "；有效期 " + cert.NotBefore.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 至 " + cert.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "；协商 " + ssl.SslProtocol; }
                                finally { cert.Dispose(); }
                            }
                        }
                    }
                    catch (AuthenticationException) { return "FAIL|TLS握手或证书验证失败；PolicyErrors=" + policy + "；ChainStatus=" + chain; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { last = e; }
                }
            }
            if (last != null) throw last;
            throw new SocketException();
        }

        static string DescribeProxy(Uri endpoint)
        {
            try
            {
                IWebProxy proxy = WebRequest.DefaultWebProxy;
                if (proxy == null) return "未配置默认代理";
                if (proxy.IsBypassed(endpoint)) return "默认代理配置存在；此地址绕过代理";
                Uri p = proxy.GetProxy(endpoint);
                return p == null || p == endpoint ? "未配置代理" : "代理 " + p.Scheme + "://" + SafeHost(p) + ":" + p.Port;
            }
            catch(Exception e) { return "FAIL|" + CloudNetworkErrors.Describe(e); }
        }

        static string SafeHost(Uri uri) { return uri == null ? "未知主机" : (uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.Host + "]" : uri.Host); }
        static string ReadWindowsVersion()
        {
            try { using (RegistryKey key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) { object product = key == null ? null : key.GetValue("ProductName"); object display = key == null ? null : key.GetValue("DisplayVersion") ?? key.GetValue("ReleaseId"); return (product == null ? "Windows" : Convert.ToString(product, CultureInfo.InvariantCulture)) + " " + (display == null ? "" : Convert.ToString(display, CultureInfo.InvariantCulture)); } }
            catch { return "不可用"; }
        }
        static int ReadDotNetReleaseNumber()
        {
            try { using (RegistryKey key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full")) { object value = key == null ? null : key.GetValue("Release"); return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); } }
            catch { return 0; }
        }
        static string ReadDotNetRelease() { int value = ReadDotNetReleaseNumber(); return value == 0 ? "不可用" : value.ToString(CultureInfo.InvariantCulture); }
        static void Add(StringBuilder b, string step, string status, string detail) { b.Append(step).Append("：").Append(status).Append(" — ").AppendLine(detail); }

        internal static int RunSelfTests()
        {
            int checks = 0;
            Action<bool> check = ok => { checks++; if (!ok) throw new InvalidOperationException("CloudDiagnostics 自测失败"); };
            check(!new CloudConfiguration { Endpoint = "http://example.com", Token = "secret-marker" }.HasValidEndpoint);
            check(!new CloudConfiguration { Endpoint = "https://user:pass@example.com/" }.HasValidEndpoint);
            check(!new CloudConfiguration { Endpoint = "https://example.com/?token=secret" }.HasValidEndpoint);
            CloudHeartbeatReply good = new CloudHeartbeatReply { SubscriptionId = "sub", LeaseSeconds = 30, RequestedPacks = new[] { 1, 16 } };
            check(IsValidLease(good));
            check(IsValidLease(new CloudHeartbeatReply { LeaseSeconds = 0, RequestedPacks = new int[0] }));
            check(!IsValidLease(new CloudHeartbeatReply { SubscriptionId = "sub", LeaseSeconds = 3601, RequestedPacks = new int[0] }));
            check(!IsValidLease(new CloudHeartbeatReply { SubscriptionId = "sub", LeaseSeconds = 1, RequestedPacks = new[] { 17 } }));
            check(!IsValidLease(new CloudHeartbeatReply { LeaseSeconds = 1, RequestedPacks = new int[0] }));
            string classification = CloudNetworkErrors.Describe(new CloudHttpException(401, "secret-marker"));
            check(!classification.Contains("secret-marker") && !classification.Contains("Bearer"));
            string tokenFile=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"cloud-token-check-"+Guid.NewGuid().ToString("N")+".txt");
            try
            {
                File.WriteAllText(tokenFile,"enabled=1\ntoken=AQ==\n");
                CloudConfiguration unreadable=CloudConfiguration.Load(tokenFile,"diagnostic-test");
                check(unreadable.SavedTokenUnreadable&&String.IsNullOrEmpty(unreadable.Token));
                string failedTokenReport=RunAsync(unreadable,CancellationToken.None).GetAwaiter().GetResult();
                check(failedTokenReport.Contains("无法由当前 Windows 用户解密")&&!failedTokenReport.Contains("AQ=="));
            }
            finally {File.Delete(tokenFile);}
            string invalid = RunAsync(new CloudConfiguration { Endpoint = "https://example.invalid/?secret-marker", Token = "secret-marker", Alias = "secret-marker", DeviceId = "secret-marker" }, CancellationToken.None).GetAwaiter().GetResult();
            check(invalid.Contains("HTTPS配置：FAIL") && !invalid.Contains("secret-marker"));
            using (CancellationTokenSource cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                string cancellation = RunAsync(new CloudConfiguration { Endpoint = "https://example.invalid", Token = "secret-marker" }, cancelled.Token).GetAwaiter().GetResult();
                check(cancellation.Contains("已取消") && !cancellation.Contains("secret-marker"));
            }
            return checks;
        }
    }
}
