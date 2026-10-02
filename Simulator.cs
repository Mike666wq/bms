using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BmsSerialDemo
{
    public static class Simulator
    {
        static int sample;
        static void U16(List<byte> b, int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        public static byte[] Respond(byte address, byte cmd, byte pack)
        {
            int tick = System.Threading.Interlocked.Increment(ref sample);
            List<byte> b = new List<byte>(); int count = pack == 255 ? 2 : 1;
            if (cmd == 0x42 || cmd == 0x44)
            {
                b.Add((byte)(pack == 255 ? count : pack));
                for (int i = 0; i < count; i++)
                {
                    b.Add(16);
                    for (int j = 0; j < 16; j++) { if (cmd == 0x42) U16(b, 3300 + i * 5 + j + (tick <= 1 ? 0 : (int)Math.Round(2 * Math.Sin(tick / 4.0)))); else b.Add(0); }
                    b.Add(6);
                    for (int j = 0; j < 6; j++) { if (cmd == 0x42) U16(b, 65 + j); else b.Add(0); }
                    if (cmd == 0x42)
                    {
                        U16(b, -123 + (tick <= 1 ? 0 : tick % 5 - 2)); U16(b, 5292 + (tick <= 1 ? 0 : tick % 3)); U16(b, 8000); b.Add(6); U16(b, 10000); U16(b, 12);
                        b.Add(80); b.Add(99); U16(b, 65535); U16(b, 45); U16(b, 65535);
                    }
                    else { b.AddRange(new byte[] { 0, 0, 0, 0, 15 }); for (int j = 0; j < 15; j++) b.Add(j == 4 ? (byte)0x80 : j == 5 ? (byte)0x03 : (byte)0); b.Add(0); }
                }
            }
            else if (cmd == 0x4d) { DateTime d = DateTime.Now; U16(b, d.Year); b.AddRange(new[] { (byte)d.Month, (byte)d.Day, (byte)d.Hour, (byte)d.Minute, (byte)d.Second }); }
            else if (cmd == 0xe9) { b.Add(pack); b.AddRange(Encoding.ASCII.GetBytes("DEMO-SIMULATED-NOT-REAL".PadRight(30, '\0'))); }
            else if (cmd == 0x4b || cmd == 0x4c) b.AddRange(new byte[] { 1, 0 });
            else return Protocol.Encode(0x52, address, 0x04, new byte[0]);
            return Protocol.Encode(0x52, address, 0, b.ToArray());
        }
    }
    static class SelfTest
    {
        static int assertions;
        static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); assertions++; }
        static void Reject(byte[] raw, string label) { bool rejected = false; try { Protocol.Decode(raw); } catch (FormatException) { rejected = true; } catch (ArgumentException) { rejected = true; } Check(rejected, label); }
        public static void Run()
        {
            try
            {
                Check(Encoding.ASCII.GetString(Protocol.Encode(0x21, 1, 0x42, new byte[] { 1 })) == "~21014642E00201FD34\r", "文档实时请求");
                Check(Encoding.ASCII.GetString(Protocol.Encode(0x21, 1, 0x44, new byte[] { 1 })) == "~21014644E00201FD32\r", "文档告警请求");
                Check(Protocol.LengthWord(18) == 0xd012, "文档 LENGTH 示例");
                byte[] doc = Encoding.ASCII.GetBytes("~52014600408401100CB90CBD0CBA0B210CB60C8C0CAC0CBF0CD70C9F0CE10CE00CE10CDF0CE10CB706003C003C003C003C003C003C0000144112C006271000003064000000000000E12C\r");
                Frame f = Protocol.Decode(doc); string layout; List<PackData> p = DataParser.Realtime(f.Info, 1, out layout);
                Check(p.Count == 1 && p[0].Cells.Length == 16 && p[0].Cells[0] == 3257, "文档响应电芯");
                Check(p[0].Voltage == 51.85 && p[0].Current == 0 && p[0].Soc == 48 && p[0].Soh == 100 && p[0].Temperatures[0] == 20, "文档响应单位");
                f = Protocol.Decode(Simulator.Respond(1, 0x42, 255)); p = DataParser.Realtime(f.Info, 255, out layout);
                Check(p.Count == 2 && p[0].Current == -1.23, "多 Pack 与负电流");
                byte[] prefixed = new byte[f.Info.Length + 1]; Array.Copy(f.Info, 0, prefixed, 1, f.Info.Length);
                Check(DataParser.Realtime(prefixed, 255, out layout).Count == 2 && layout == "含 INFOFLAG", "带 INFOFLAG 布局");
                Check(DataParser.Alarm(Protocol.Decode(Simulator.Respond(1, 0x44, 1)).Info, 1).Contains("状态15"), "告警布局");
                byte[] broken = (byte[])doc.Clone(); broken[broken.Length - 3] = (byte)'0'; Reject(broken, "错误 CHKSUM");
                broken = (byte[])doc.Clone(); broken[9] = (byte)'5'; Reject(broken, "错误 LCHKSUM");
                broken = (byte[])doc.Clone(); broken[15] = (byte)'G'; Reject(broken, "非法 ASCII");
                Reject(Encoding.ASCII.GetBytes("~21014600E002FD34\r"), "截断帧");
                int frames = 0; Framer framer = new Framer { Complete = delegate(byte[] raw) { Protocol.Decode(raw); frames++; } };
                framer.Feed(new byte[] { 0, 1, 2 }); foreach (byte value in doc) framer.Feed(new[] { value });
                byte[] doubled = new byte[doc.Length * 2]; Array.Copy(doc, doubled, doc.Length); Array.Copy(doc, 0, doubled, doc.Length, doc.Length); framer.Feed(doubled);
                Check(frames == 3, "噪声 半包 粘包");
                framer.Feed(Encoding.ASCII.GetBytes("~BAD")); framer.Feed(doc); Check(frames == 4, "新帧头重同步");
                bool rejectedData = false; try { DataParser.Realtime(new byte[] { 1, 255 }, 1, out layout); } catch (FormatException) { rejectedData = true; } Check(rejectedData, "载荷越界");
                MainForm.SmokeTest(); Check(true, "界面初始化、模拟实时和告警读取、断开");
                Check(StyledActionButton.VerifyRepeatedDrawing()==1,"原生主按钮重复绘制/GDI句柄释放");
                Check(StyledCheckBox.VerifyToggleSemantics(),"复选框鼠标/键盘控件状态语义");
                int uiChecks=UiLayoutTests.Run();for(int i=0;i<uiChecks;i++)Check(true,"真实样式/缩放/控件布局");
                int destinationChecks=ExportDestinationTests.Run();for(int i=0;i<destinationChecks;i++)Check(true,"可写目标/提交/失败清理");
                int catalogChecks=MonthlyCatalog.RunSelfTests();for(int i=0;i<catalogChecks;i++)Check(true,"月库跨库分页/旧库/重复ID/UTC排序");
                int partitionChecks=PartitionCycleTests.Run();for(int i=0;i<partitionChecks;i++)Check(true,"分期边界/重启/来源隔离/配置持久化");
                int integrationChecks=MainForm.RunLocalIntegrationTests();for(int i=0;i<integrationChecks;i++)Check(true,"整轮切库/身份元数据/跨周期查询与导出");
                int cloudChecks=CloudRealtimeService.RunSelfTests();for(int i=0;i<cloudChecks;i++)Check(true,"云端配置/DPAPI/假租约/有界上传");
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-result.txt"), "PASS: " + assertions + " checks (including headless UI simulation)\r\n" + DateTime.Now.ToString("O"), Encoding.UTF8);
                Environment.ExitCode = 0;
            }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-result.txt"), e.ToString(), Encoding.UTF8); Environment.ExitCode = 1;
            }
        }
    }
}
