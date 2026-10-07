using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows.Forms;

[assembly: AssemblyVersion("1.2.6.0")]
[assembly: AssemblyFileVersion("1.2.6.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName=".NET Framework 4.8")]

namespace BmsSerialDemo
{
    static class Program
    {
        static bool diagnosticMode;
        static bool applicationStylesConfigured;
        [System.Runtime.InteropServices.DllImport("shcore.dll", PreserveSig=true)] static extern int SetProcessDpiAwareness(int awareness);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        internal static void ConfigureApplicationStylesAndDpi()
        {
            if(applicationStylesConfigured)return;
            // System-aware DPI keeps WinForms coordinates in physical pixels at the startup monitor's scale.
            // Call before any HWND is created; unsupported/older Windows falls back to the Win32 API.
            bool dpiSet=false;
            try{dpiSet=SetProcessDpiAwareness(1)==0;}catch{ }
            if(!dpiSet)try{SetProcessDPIAware();}catch{ }
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);applicationStylesConfigured=true;
        }
        [STAThread] static void Main(string[] args)
        {
            ConfigureApplicationStylesAndDpi();
            diagnosticMode = args.Length > 0 && (args[0] == "--cloud-probe" || args[0] == "--self-test" || args[0] == "--test-ui-layout" || args[0] == "--preview-ui" || args[0] == "--capture-ui");
            // 诊断/预览模式同样注册全局异常钩子：仅记录日志，不弹窗、不改变退出行为，保证自动化运行可观察失败原因。
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) { CrashLogger.Write("AppDomain unhandled exception; terminating=" + e.IsTerminating + (diagnosticMode ? "; mode=diagnostic" : ""), e.ExceptionObject as Exception); };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                CrashLogger.Write("UI thread exception" + (diagnosticMode ? "; mode=diagnostic" : ""), e.Exception);
                if(diagnosticMode)return;
                try { MessageBox.Show("界面发生未处理错误，程序将安全退出。诊断日志已保存到 logs 目录。", "BMS 运行错误", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
                Application.Exit();
            };
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e) {
                if (String.Equals(new AssemblyName(e.Name).Name,"System.Data.SQLite",StringComparison.OrdinalIgnoreCase)) {
                    string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib", "System.Data.SQLite.dll");
                    if (File.Exists(local)) return Assembly.LoadFrom(local);
                }
                return null;
            };
            if(args.Length>0&&args[0]=="--cloud-probe"){try{Uri probe=new Uri(args.Length>1?args[1]:"https://pv-ac.bbben.xyz");string result=CloudDiagnostics.RunEndpointProbeAsync(probe,CancellationToken.None).GetAwaiter().GetResult();File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"cloud-probe-result.txt"),result,new UTF8Encoding(true));}catch(Exception e){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"cloud-probe-result.txt"),CloudNetworkErrors.Describe(e),new UTF8Encoding(true));Environment.ExitCode=1;}return;}
            if (diagnosticMode) { if(args[0] == "--self-test") { SelfTest.Run(); return; } if(args[0] == "--test-ui-layout") { int checks=UiLayoutTests.Run();File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-layout-test-result.txt"),"PASS: "+checks+" UI layout checks (125%/150% simulated; Windows display scaling unchanged).\r\n",Encoding.UTF8);return; } int duration=6500;if(args.Length>2)Int32.TryParse(args[2],out duration);MainForm.CapturePreview(args.Length > 1 ? args[1] : "1360x920",Math.Max(6500,Math.Min(600000,duration))); return; }
            try { Application.Run(new MainForm()); }
            catch(Exception e) { CrashLogger.Write("Startup/main loop exception", e); try { MessageBox.Show("程序遇到未处理错误，将退出。诊断日志已保存到 logs 目录。", "BMS 运行错误", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { } }
        }
    }
    static class CrashLogger
    {
        static readonly object sync = new object();
        internal static void Write(string exitType, Exception error)
        {
            try {
                string body = "时间: " + DateTime.Now.ToString("o") + "\r\n退出类型: " + exitType + "\r\n版本: " + Assembly.GetExecutingAssembly().GetName().Version + "\r\n运行时: " + Environment.Version + "\r\n位数: " + (Environment.Is64BitProcess ? "64-bit" : "32-bit") + "\r\n操作系统: " + Environment.OSVersion + "\r\n异常:\r\n" + ExceptionText(error) + "\r\n";
                string name = "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log";
                lock(sync) { try { string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"); Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, name), body, Encoding.UTF8); }
                    catch { string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BmsSerialDemo", "logs"); Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, name), body, Encoding.UTF8); } }
            } catch { }
        }
        static string ExceptionText(Exception error)
        {
            if(error==null)return "<无异常对象>";
            try{return error.ToString();}catch{
                string type="<类型不可读>",message="<消息不可读>",stack="<调用栈不可读>";
                try{type=error.GetType().FullName;}catch{}try{message=error.Message;}catch{}try{stack=error.StackTrace;}catch{}
                return type+"\r\n"+message+"\r\n"+stack+"\r\n（完整异常格式化失败，已保留可读取的诊断字段）";
            }
        }
    }
    sealed class Request
    {
        public byte Command, Pack, Address;
        public long AcquisitionRound; public int? PeriodSeconds;
        public TaskCompletionSource<Frame> Completion = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed partial class MainForm : Form
    {
        readonly ComboBox ports = new ComboBox { Width = 105, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly NumericUpDown baud = Number(9600, 1200, 115200, 95), address = Number(1, 1, 254, 55), pack = Number(1, 1, 16, 50), period = Number(2, 1, 86400, 85), timeout = Number(1500, 100, 60000, 85);
        readonly StyledCheckBox all = new StyledCheckBox { Text = "全部 Pack", Checked = false }, simulate = new StyledCheckBox { Text = "模拟设备", Checked = true }, save = new StyledCheckBox { Text = "记录原始收发日志", Checked = false };
        readonly StyledActionButton connect = new StyledActionButton { Text = "连接", IconGlyph = "●", Width = 104, Height = 34 };
        readonly StyledActionButton poll = new StyledActionButton { Text = "开始轮询", IconGlyph = "▶", Width = 144, Height = 32 };
        readonly ComboBox command = new ComboBox { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly NumericUpDown historyAction = Number(0, 0, 3, 45);
        readonly TextBox details = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = DetailsFont, WordWrap = false };
        readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = LogFont, WordWrap = false };
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 28, Text = "未连接。默认模拟模式，可先验证显示和解析。" };
        readonly Label connectionState = new Label { AutoSize = true, Text = "● 未连接", ForeColor = Color.Gray, Font = BoldBodyFont };
        readonly Label[] metrics = new Label[7];
        readonly Label spread = new Label { AutoSize = true, ForeColor = Color.DarkSlateGray };
        readonly FlowLayoutPanel cellCards = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, Padding = new Padding(4) };
        readonly FlowLayoutPanel tempCards = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, Padding = new Padding(4) };
        readonly ListBox alarmList = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, Font = BodyFont };
        readonly FlowLayoutPanel runtimeStates = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true, Padding = new Padding(2) };
        readonly TrendControl trend = new TrendControl();
        readonly List<Panel> cellCardCache = new List<Panel>();
        readonly List<Label> cellTagCache = new List<Label>();
        readonly List<Label> cellValueCache = new List<Label>();
        readonly List<ProgressBar> cellBarCache = new List<ProgressBar>();
        readonly List<Panel> tempCardCache = new List<Panel>();
        readonly List<Label> tempValueCache = new List<Label>();
        readonly List<Label> runtimeChipCache = new List<Label>();
        readonly Label tempNote = new Label { Text = "5/6物理标签待确认", AutoSize = true, ForeColor = Color.FromArgb(122,137,153), Margin = new Padding(4) };
        Label cellsEmpty;
        Label tempsEmpty;
        readonly Font cellTagFont = new Font("Microsoft YaHei UI", 7.5f);
        readonly Font cellValueFont = new Font("Microsoft YaHei UI", 9, FontStyle.Bold);
        readonly Font tempTagFont = new Font("Microsoft YaHei UI", 8);
        readonly Font tempValueFont = new Font("Microsoft YaHei UI", 11, FontStyle.Bold);
        // C32：静态共享字体——窗体/预览反复重建不再累积 GDI 句柄；控件 Dispose 不释放 Font，共享安全。
        static readonly Font DetailsFont = new Font("Consolas", 10), LogFont = new Font("Consolas", 9);
        static readonly Font BodyFont = new Font("Microsoft YaHei UI", 9), BoldBodyFont = new Font("Microsoft YaHei UI", 9, FontStyle.Bold), SectionFont = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), BrandFont = new Font("Microsoft YaHei UI", 12, FontStyle.Bold), PageTitleFont = new Font("Microsoft YaHei UI", 15, FontStyle.Bold);
        readonly Dictionary<int, AlarmSnapshot> alarmSnapshotByPack = new Dictionary<int, AlarmSnapshot>();
        readonly Dictionary<int, RealtimeSnapshot> latestSnapshots = new Dictionary<int, RealtimeSnapshot>();
        readonly Dictionary<string, SampleStore> stores = new Dictionary<string, SampleStore>(StringComparer.OrdinalIgnoreCase);
        SampleStore activeStore;
        StoragePage storagePage;
        TabControl mainTabs;
        IRealtimePublisher publisher;
        CloudRealtimeService cloudPublisher;
        CloudConfiguration cloudConfiguration;
        CloudPage cloudPage;
        readonly string cloudSettingsPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings","cloud-connection.txt");
        bool previewMode;
        // C2：诊断截图/预览使用隔离数据根（临时目录）时置位——禁止把模拟快照发布到真实云端。
        readonly bool diagnosticIsolation;
        readonly List<Task> pendingStoreDrains = new List<Task>();
        string displaySource;
        string recordShutdownError;
        Panel cellsSection;
        Label cellSectionHeader;
        StyledActionButton livePollButton;
        readonly Dictionary<int, DateTime> freshness = new Dictionary<int, DateTime>();
        readonly Dictionary<int, DateTime> alarmFreshness = new Dictionary<int, DateTime>();
        readonly Dictionary<int, List<string>> alarmByPack = new Dictionary<int, List<string>>();
        readonly Dictionary<int, List<string>> runtimeByPack = new Dictionary<int, List<string>>();
        readonly ComboBox viewPack = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
        PackData[] latestPacks = new PackData[0];
        int pollSuccess, pollFailure, pollElapsed;
        DateTime lastUpdate = DateTime.MinValue;
        DateTime lastAlarmUpdate = DateTime.MinValue;
        DateTime requestStarted;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        readonly Framer framer = new Framer();
        readonly object receiveLock = new object();
        SerialPort serial;
        Request pending;
        bool connected, polling, simulatedConnection, closing;
        volatile bool rawByteDiagnostics;
        int generation;
        DateTime quarantineUntil = DateTime.MinValue;
        StreamWriter writer;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer freshnessTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        bool disposedResources;
        bool cloudDisposed;
        protected override void Dispose(bool disposing){bool disposeOwned=disposing&&!disposedResources;if(disposeOwned){disposedResources=true;timer.Stop();timer.Dispose();freshnessTimer.Stop();freshnessTimer.Dispose();DisposeCloudPublisher();}base.Dispose(disposing);if(disposeOwned){tempNote.Dispose();cellTagFont.Dispose();cellValueFont.Dispose();tempTagFont.Dispose();tempValueFont.Dispose();}}
        void DisposeCloudPublisher(){if(cloudDisposed)return;cloudDisposed=true;if(cloudPublisher!=null)cloudPublisher.Dispose();}
        readonly Dictionary<int, int> rows = new Dictionary<int, int>();
        long nextPollTicks, acquisitionRound, activeRound; int activeRoundPeriodSeconds=2, skippedRounds; bool pollCycleBusy;
        readonly string periodSettingsPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings","recording-period.txt");
        readonly string dataRoot=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data");
        readonly Dictionary<long,string> roundDatabase = new Dictionary<long,string>();
        string deviceId;
        MonthlyCatalog monthlyCatalog;
        protected override bool ShowWithoutActivation { get { return true; } }
        static NumericUpDown Number(decimal value, decimal min, decimal max, int width) { return new NumericUpDown { Minimum = min, Maximum = max, Value = value, Width = width }; }
        public MainForm() : this(null,null) { }
        internal MainForm(string testDataRoot,string testPeriodSettingsPath)
        {
            if(!String.IsNullOrEmpty(testDataRoot)){dataRoot=testDataRoot;cloudSettingsPath=Path.Combine(testDataRoot,"settings","cloud-connection.txt");diagnosticIsolation=true;}
            if(!String.IsNullOrEmpty(testPeriodSettingsPath))periodSettingsPath=testPeriodSettingsPath;
            AutoScaleMode=AutoScaleMode.Dpi;Text = "BMS 实时监控 1.2.6"; Width = 1360; Height = 920; MinimumSize = new Size(1050, 720); BackColor = UiTheme.Canvas;
            Font = BodyFont; ForeColor = Color.FromArgb(40, 56, 66);
            deviceId=LoadDeviceId();
            InitializePartitionManagers();
            monthlyCatalog=new MonthlyCatalog(dataRoot,deviceId);
            cloudConfiguration=CloudConfiguration.Load(cloudSettingsPath,deviceId);cloudConfiguration.DeviceId=deviceId;cloudPublisher=new CloudRealtimeService(delegate{return cloudConfiguration;},null,ReadCloudHistory);publisher=cloudPublisher;
            LoadPeriodSettings(testDataRoot==null);
            FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true, Padding = new Padding(8, 5, 8, 3), BackColor = Color.FromArgb(247, 250, 253) };
            Add(bar, "串口", ports); Button refresh = new Button { Text = "刷新", Width = 55, Height = 26, FlatStyle = FlatStyle.Flat, BackColor = Color.White }; bar.Controls.Add(refresh);
            Add(bar, "波特率", baud); Add(bar, "地址", address); Add(bar, "Pack", pack); bar.Controls.Add(all); bar.Controls.Add(simulate);
            Panel masthead = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 57, 91) };
            Label brand = new Label { Text = "BMS  /  实时监控  ·  1.2.6", AutoSize = true, Font = BrandFont, ForeColor = Color.White, Location = new Point(18, 9) };
            connect.Anchor = AnchorStyles.Top | AnchorStyles.Right; connect.BackColor=Color.FromArgb(65,111,232);connect.ForeColor=Color.White;masthead.Controls.Add(connect); masthead.Controls.Add(brand);
            masthead.Resize += delegate { connect.Location = new Point(masthead.ClientSize.Width - connect.Width - 16, 7); };
            TableLayoutPanel shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.FromArgb(239, 244, 249) };
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            shell.Controls.Add(masthead, 0, 0); shell.Controls.Add(bar, 0, 1);
            command.Items.AddRange(new object[] { "42 实时数据", "44 当前告警", "4D 读取时间", "E9 序列号", "47 系统参数（原始）", "4B 设备历史（原始）", "4C 历史告警（原始）" }); command.SelectedIndex = 0;
            StyledActionButton read = new StyledActionButton { Text = "发送读取", IconGlyph = "⌕", Width = 144, Height = 32, BackColor=Color.FromArgb(65,111,232) };
            Button clear = new Button { Text = "清空显示", AutoSize = true };
            TabControl tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4), DrawMode = TabDrawMode.OwnerDrawFixed, ItemSize = new Size(112, 34), SizeMode = TabSizeMode.Fixed };mainTabs=tabs;
            tabs.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                bool selected=(e.State&DrawItemState.Selected)==DrawItemState.Selected;
                using(SolidBrush bg=new SolidBrush(selected?Color.White:Color.FromArgb(247,250,253)))e.Graphics.FillRectangle(bg,e.Bounds);
                string label=tabs.TabPages[e.Index].Text;
                TextRenderer.DrawText(e.Graphics,label,tabs.Font,e.Bounds,selected?UiTheme.Blue:UiTheme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
                if(selected)using(Pen pen=new Pen(UiTheme.Blue,3f))e.Graphics.DrawLine(pen,e.Bounds.Left+18,e.Bounds.Bottom-2,e.Bounds.Right-18,e.Bounds.Bottom-2);
            };
            TabPage liveTab = new TabPage("实时总览") { BackColor = Color.FromArgb(242, 247, 248) };
            TabPage recordTab = new TabPage("数据记录") { BackColor = Color.FromArgb(242, 247, 248) };
            TabPage cloudTab = new TabPage("云端连接") { BackColor = Color.FromArgb(239, 244, 249) };
            TabPage diagTab = new TabPage("通信诊断") { BackColor = Color.White };
            tabs.TabPages.Add(liveTab); tabs.TabPages.Add(recordTab); tabs.TabPages.Add(cloudTab); tabs.TabPages.Add(diagTab); shell.Controls.Add(tabs, 0, 2);
            status.Dock = DockStyle.Fill; status.Height = 25; status.Padding = new Padding(10, 3, 0, 0); status.BackColor = Color.FromArgb(229, 237, 246); status.ForeColor = Color.FromArgb(71, 91, 113); shell.Controls.Add(status, 0, 3); Controls.Add(shell);
            BuildLivePage(liveTab);
            storagePage = new StoragePage(GetStoreForPage,monthlyCatalog); storagePage.Dock = DockStyle.Fill; BuildRecordingPage(recordTab);
            cloudPage=new CloudPage(delegate{return cloudConfiguration;},delegate(CloudConfiguration c){cloudConfiguration=c;},cloudPublisher);cloudPage.Dock=DockStyle.Fill;cloudTab.Controls.Add(cloudPage);
            tabs.SelectedIndexChanged += delegate { rawByteDiagnostics=save.Checked||tabs.SelectedTab==diagTab;if (tabs.SelectedTab == recordTab) storagePage.EnterPage(activeStore == null ? null : activeStore.Source); };
            save.CheckedChanged += delegate { rawByteDiagnostics=save.Checked||tabs.SelectedTab==diagTab; };
            FlowLayoutPanel diagBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, Padding = new Padding(6), WrapContents = true, AutoScroll=true, BackColor=Color.White };
            Add(diagBar, "超时 ms", timeout); diagBar.Controls.Add(poll); diagBar.Controls.Add(save);
            Add(diagBar, "单次读取", command); Add(diagBar, "历史动作", historyAction); diagBar.Controls.Add(read); diagBar.Controls.Add(clear);
            GroupBox diagControls=new GroupBox{Text="读取控制",Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(8),BackColor=Color.White,ForeColor=Color.FromArgb(34,70,108),Font=BoldBodyFont};diagControls.Controls.Add(diagBar);
            SplitContainer outer = new SplitContainer { Size = new Size(1200, 600), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 310, BackColor = UiTheme.Canvas, BorderStyle=BorderStyle.None, SplitterWidth=6 };
            SplitContainer inner = new SplitContainer { Size = new Size(1200, 310), Dock = DockStyle.Fill, SplitterDistance = 600, BackColor = UiTheme.Canvas, BorderStyle=BorderStyle.None, SplitterWidth=6 };
            GroupBox liveGroup=new GroupBox{Text="实时响应概览",Dock=DockStyle.Fill,BackColor=Color.White,ForeColor=Color.FromArgb(34,70,108),Font=new Font("Microsoft YaHei UI",9,FontStyle.Bold),Padding=new Padding(8)};liveGroup.Controls.Add(grid);
            GroupBox detailGroup=new GroupBox{Text="响应详情",Dock=DockStyle.Fill,BackColor=Color.White,ForeColor=Color.FromArgb(34,70,108),Font=new Font("Microsoft YaHei UI",9,FontStyle.Bold),Padding=new Padding(8)};detailGroup.Controls.Add(details);
            GroupBox logGroup=new GroupBox{Text="通信日志",Dock=DockStyle.Fill,BackColor=Color.White,ForeColor=Color.FromArgb(34,70,108),Font=new Font("Microsoft YaHei UI",9,FontStyle.Bold),Padding=new Padding(8)};logGroup.Controls.Add(log);
            outer.Panel1.Controls.Add(inner); inner.Panel1.Controls.Add(liveGroup); inner.Panel2.Controls.Add(detailGroup); outer.Panel2.Controls.Add(logGroup); diagTab.Controls.Add(outer);diagTab.Controls.Add(diagControls);
            foreach (string name in new[] { "Pack", "更新时间", "总压 V", "电流 A", "SOC %", "SOH %", "剩余 Ah", "总容量 Ah", "循环" }) grid.Columns.Add(name, name);
            grid.Columns[1].MinimumWidth=120;grid.Columns[1].FillWeight=125;
            refresh.Click += delegate { RefreshPorts(); }; connect.Click += delegate { ToggleConnection(); };
            poll.Click += delegate { TogglePolling(); };
            period.ValueChanged += delegate { PeriodChanged(); };
            timer.Interval=100; timer.Tick += delegate { PollTick(); };
            freshnessTimer.Tick += delegate { CheckFreshness(); };
            read.Click += async delegate { if (polling) { Notice("请先停止轮询，再执行单次读取"); return; } await ReadOnce(); };
            clear.Click += delegate { log.Clear(); details.Clear(); grid.Rows.Clear(); rows.Clear(); };
            for (int pi = 1; pi <= 16; pi++) viewPack.Items.Add("Pack " + pi); viewPack.SelectedIndex = 0;
            viewPack.SelectedIndexChanged += delegate { trend.SelectedPack = viewPack.SelectedIndex + 1; RenderPack(); RenderAlarm(); };
            framer.Complete = ReceiveFrame; framer.Invalid = delegate(string error, byte[] raw) { Post(delegate { Log("帧错误 " + error + " HEX=" + Protocol.Hex(raw)); }); };
            FormClosing += delegate { closing = true; Disconnect();DisposeCloudPublisher(); Task[] pendingDrains;lock(pendingStoreDrains)pendingDrains=pendingStoreDrains.ToArray();if(pendingDrains.Length>0)try{Task.WaitAll(pendingDrains,15000);}catch{} List<string> errors=new List<string>();if(!String.IsNullOrEmpty(recordShutdownError))errors.Add(recordShutdownError);foreach (SampleStore db in stores.Values) try { db.Dispose(); } catch (Exception e) { errors.Add("本地记录库关闭失败，待写数据可能未完整落盘："+e.Message); CrashLogger.Write("safe shutdown storage exception",e); } stores.Clear(); activeStore=null;if(errors.Count>0&&!previewMode)MessageBox.Show(this,String.Join("\r\n",errors),"关闭记录错误",MessageBoxButtons.OK,MessageBoxIcon.Error); };
            RefreshPorts(); UiTheme.Apply(this);
        }
        string LoadDeviceId()
        {
            // 只读安装目录或受限账户下不得抛异常阻断启动：主路径读取→备用路径读取→新生成并尽力持久化→内存回退。
            // 读取校验与 PartitionCycleManager 的要求一致（16-64 位字母/数字/下划线/连字符），避免无效内容在后续构造时才失败。
            string primary=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings","device-id.txt");
            string fallback=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BmsSerialDemo","settings","device-id.txt");
            string value;
            if(TryReadDeviceId(primary,out value))return value;
            if(TryReadDeviceId(fallback,out value))return value;
            string id=Guid.NewGuid().ToString("N");
            if(TryWriteDeviceId(primary,id))return id;
            if(TryWriteDeviceId(fallback,id))return id;
            CrashLogger.Write("设备标识无法持久化，本次运行使用内存标识（重启后会生成新标识）",new IOException(primary+" 与 "+fallback+" 均不可写"));
            return id;
        }
        static readonly Regex DeviceIdPattern=new Regex("^[A-Za-z0-9_-]{16,64}$",RegexOptions.CultureInvariant);
        static bool TryReadDeviceId(string path,out string value)
        {
            value=null;
            try
            {
                if(!File.Exists(path))return false;
                string candidate=File.ReadAllText(path).Trim();
                if(DeviceIdPattern.IsMatch(candidate)){value=candidate;return true;}
                CrashLogger.Write("设备标识文件内容无效，已忽略："+path,new FormatException("device-id 必须为 16-64 位字母/数字/下划线/连字符"));
            }
            catch(Exception e){CrashLogger.Write("设备标识文件读取失败："+path,e);}
            return false;
        }
        static bool TryWriteDeviceId(string path,string id)
        {
            try{Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,id,new UTF8Encoding(false));return true;}
            catch(Exception e){CrashLogger.Write("设备标识文件写入失败："+path,e);return false;}
        }
        void LoadPeriodSettings(bool allowLegacyImport)
        {
            int saved;try{saved=Int32.Parse(File.ReadAllText(periodSettingsPath).Trim());if(saved>=1&&saved<=86400){period.Value=saved;return;}}catch{}
            if(!allowLegacyImport)return;string legacy=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BmsSerialDemo","recording-period.txt");try{saved=Int32.Parse(File.ReadAllText(legacy).Trim());if(saved<1||saved>86400)return;period.Value=saved;Directory.CreateDirectory(Path.GetDirectoryName(periodSettingsPath));File.WriteAllText(periodSettingsPath,saved.ToString(System.Globalization.CultureInfo.InvariantCulture),new UTF8Encoding(false));}catch{}
        }
        string MonthDatabasePath(string source,DateTime utc)
        {
            return GetPartitionManager(source).DatabasePath(utc);
        }
        SampleStore ActivateMonthlyStore(string source,DateTime utc,long round)
        {
            PartitionDescriptor descriptor=GetPartitionManager(source).Describe(utc);
            if(round>0)roundPartitions[round]=descriptor;
            return ActivateStorePath(source,descriptor.DatabasePath,round);
        }
        IList<StoredSample> ReadCloudHistory(string source,DateTime fromUtc,DateTime toUtc,int pack)
        {
            List<StoredSample> rows=new List<StoredSample>();StoredSample cursor=null;
            for(int pageNumber=0;pageNumber<20;pageNumber++)
            {
                IList<StoredSample> page=monthlyCatalog.QueryPage(source,fromUtc,toUtc,pack,cursor,500);if(page.Count==0)break;rows.AddRange(page);cursor=page[page.Count-1];if(page.Count<500)break;
            }
            return rows;
        }
        SampleStore ActivateStorePath(string source,string path,long round)
        {
            SampleStore store;
            if(activeStore!=null&&!String.Equals(activeStore.DatabasePath,path,StringComparison.OrdinalIgnoreCase))
            {
                SampleStore old=activeStore;stores.Remove(old.Source);activeStore=null;
                // C4：旧库排空/关闭移出 UI 线程——新周期样本立即写入新库（不同路径、各自独立 writer）；
                // 排空超时只记录诊断并提示，绝不等同写失败、绝不触发 StopForRecordingFailure。
                Task drain=Task.Run(delegate{
                    Exception drainError=null;
                    try{old.StopSessionAsync().GetAwaiter().GetResult();}catch(Exception e){drainError=e;}
                    try{old.Dispose();}catch(Exception e){if(drainError==null)drainError=e;}
                    if(drainError!=null){CrashLogger.Write("旧记录库收尾失败（新库记录未中断）",drainError);Post(delegate{Notice("上一周期记录库收尾失败（新库记录不受影响）："+drainError.Message);});}
                });
                lock(pendingStoreDrains)pendingStoreDrains.Add(drain);
            }
            if(!stores.TryGetValue(source,out store))
            {
                store=new SampleStore(source,path,true,false);
                try {
                    store.Ready.GetAwaiter().GetResult();
                    PartitionDescriptor descriptor;
                    if(round>0&&roundPartitions.TryGetValue(round,out descriptor))store.SetPartitionInfoAsync(deviceId,descriptor.StartUtc,descriptor.EndUtc,descriptor.Days,descriptor.Epoch).GetAwaiter().GetResult();
                    if(connected) { store.StartSessionAsync((byte)address.Value,simulatedConnection?"模拟连接":"串口连接").GetAwaiter().GetResult();store.RecordPolicyChangeAsync((int)period.Value).GetAwaiter().GetResult(); }
                    stores[source]=store;
                } catch { try{store.Dispose();}catch{}throw; }
            }
            if(connected&&!store.GetStatus().IsRecording) { store.StartSessionAsync((byte)address.Value,simulatedConnection?"模拟连接":"串口连接").GetAwaiter().GetResult();store.RecordPolicyChangeAsync((int)period.Value).GetAwaiter().GetResult(); }
            activeStore=store;if(round>0){roundDatabase[round]=path;if(roundDatabase.Count>128){long min=Int64.MaxValue;foreach(long k in roundDatabase.Keys)if(k<min)min=k;if(min!=Int64.MaxValue)roundDatabase.Remove(min);}}
            return store;
        }
        void BuildLivePage(TabPage page)
        {
            Panel viewport=new Panel{Dock=DockStyle.Fill,AutoScroll=true,BackColor=Color.FromArgb(239,244,249)};
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 4, Padding = new Padding(12, 9, 12, 8), Margin=Padding.Empty, BackColor = Color.FromArgb(239, 244, 249) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,250)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            viewport.Controls.Add(layout);page.Controls.Add(viewport);
            FlowLayoutPanel head = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = false, BackColor = Color.Transparent, Padding = new Padding(1, 1, 1, 1) };
            head.Controls.Add(new Label { Text = "实时总览", Font = PageTitleFont, AutoSize = true, ForeColor = Color.FromArgb(25, 52, 82), Margin = new Padding(0, 1, 20, 0) });
            livePollButton = new StyledActionButton { Text = "开始实时采集", IconGlyph="▶", Width = 166, Height = 34, BackColor = Color.FromArgb(65,111,232), ForeColor = Color.White, Margin = new Padding(0, 0, 16, 0) };
            livePollButton.Click += delegate { TogglePolling(); };
            head.Controls.Add(livePollButton); head.Controls.Add(new Label{Text="采集/记录间隔 s",AutoSize=true,ForeColor=Color.FromArgb(89,108,130),Margin=new Padding(4,7,4,0)});head.Controls.Add(new RoundedInputHost(period){Width=100,Height=36,Margin=new Padding(2,0,7,0)}); viewPack.Width = 105; head.Controls.Add(new Label { Text = "显示 Pack", AutoSize = true, ForeColor = Color.FromArgb(89, 108, 130), Margin = new Padding(0, 7, 5, 0) }); head.Controls.Add(new RoundedInputHost(viewPack){Width=120,Height=36,Margin=new Padding(0,0,2,0)});
            connectionState.ForeColor = Color.FromArgb(60, 105, 149); connectionState.Margin = new Padding(12, 7, 0, 0);connectionState.AutoSize=false;connectionState.Width=188;connectionState.Height=24;connectionState.AutoEllipsis=true; head.Controls.Add(connectionState);
            layout.Controls.Add(head, 0, 0);
            FlowLayoutPanel cards = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll=true, BackColor = Color.Transparent, Padding = new Padding(0, 3, 0, 2) };
            string[] names = { "总压", "电流", "SOC", "SOH", "剩余容量", "总容量", "循环次数" };
            for (int i = 0; i < names.Length; i++) { Panel card = MetricCard(names[i], out metrics[i]);card.Dock=DockStyle.None;card.Width=cards.ClientSize.Width>0?Math.Max(132,(cards.ClientSize.Width-28)/7):160;card.Height=78;cards.Controls.Add(card); }
            layout.Controls.Add(cards, 0, 1);
            SplitContainer mid = new SplitContainer { Size = new Size(1200, 250), Dock = DockStyle.Fill, SplitterDistance = 590, Panel1MinSize=230, Panel2MinSize=230, BackColor = Color.FromArgb(239, 244, 249), BorderStyle = BorderStyle.None, SplitterWidth=6 };
            cellCards.AutoScroll = true; cellCards.WrapContents = true; cellCards.Padding = new Padding(4); cellsSection = Section("电芯电压", cellCards); cellSectionHeader = (Label)((TableLayoutPanel)cellsSection.Controls[0]).GetControlFromPosition(0, 0);
            TableLayoutPanel alarmBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White, Padding = new Padding(1) }; alarmBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 76)); alarmBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            runtimeStates.Padding = new Padding(2); alarmList.DrawMode = DrawMode.OwnerDrawFixed; alarmList.ItemHeight = 24; alarmList.DrawItem += DrawAlarmItem; alarmBody.Controls.Add(runtimeStates, 0, 0); alarmBody.Controls.Add(alarmList, 0, 1);
            Panel alarm = Section("告警与运行状态", alarmBody);
            mid.Panel1.Controls.Add(cellsSection); mid.Panel2.Controls.Add(alarm); layout.Controls.Add(mid, 0, 2);
            SplitContainer low = new SplitContainer { Size = new Size(1200, 310), Dock = DockStyle.Fill, SplitterDistance = 720, Panel1MinSize=360, Panel2MinSize=220, BackColor = Color.FromArgb(239, 244, 249), BorderStyle = BorderStyle.None, SplitterWidth=6 };
            tempCards.AutoScroll = true; tempCards.WrapContents = true; Panel temps = Section("温度测点", tempCards); Panel chartContent = new Panel { Dock = DockStyle.Fill, BackColor = Color.White }; Panel chartPanel = Section("实时趋势", chartContent);
            FlowLayoutPanel trendHead = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 27, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Color.White };
            ComboBox series = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 }; series.Items.AddRange(new object[] { "总压", "电流", "SOC" }); series.SelectedIndex = 0; series.SelectedIndexChanged += delegate { trend.SeriesIndex = series.SelectedIndex; };
            ComboBox pointCount = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 }; pointCount.Items.AddRange(new object[] { "120点", "300点", "600点" }); pointCount.SelectedIndex = 0; pointCount.SelectedIndexChanged += delegate { trend.Capacity = pointCount.SelectedIndex == 0 ? 120 : pointCount.SelectedIndex == 1 ? 300 : 600; };
            trendHead.Controls.Add(new RoundedInputHost(pointCount){Width=88,Height=32,Margin=new Padding(2,0,4,0)});trendHead.Controls.Add(new RoundedInputHost(series){Width=112,Height=32,Margin=new Padding(2,0,4,0)}); chartContent.Controls.Add(trend); chartContent.Controls.Add(trendHead); trend.Dock = DockStyle.Fill;
            low.Panel1.Controls.Add(chartPanel); low.Panel2.Controls.Add(temps); layout.Controls.Add(low, 0, 3);
            bool resizingLayout=false;Action resizeLayout=null;
            resizeLayout=delegate
            {
                if(resizingLayout||viewport.ClientSize.Width<=0)return;resizingLayout=true;
                try
                {
                    int targetWidth=viewport.ClientSize.Width-(viewport.VerticalScroll.Visible?SystemInformation.VerticalScrollBarWidth:0);
                    if(layout.Width!=targetWidth){layout.Width=targetWidth;layout.PerformLayout();}
                    int cardColumns=Math.Min(7,Math.Max(1,cards.ClientSize.Width/180));int cardWidth=Math.Max(174,(cards.ClientSize.Width-8*cardColumns)/cardColumns);
                    foreach(Control card in cards.Controls)if(card.Width!=cardWidth)card.Width=cardWidth;
                    int cardRows=(7+cardColumns-1)/cardColumns;int cardsHeight=Math.Max(88,cardRows*82+6);
                    if((int)layout.RowStyles[1].Height!=cardsHeight)layout.RowStyles[1].Height=cardsHeight;
                    float headerHeight=head.ClientSize.Width<1020?76:40;
                    if(layout.RowStyles[0].Height!=headerHeight)layout.RowStyles[0].Height=headerHeight;
                    int minHeight=(int)(layout.RowStyles[0].Height+layout.RowStyles[1].Height+layout.RowStyles[2].Height)+340+layout.Padding.Vertical;
                    int desiredHeight=Math.Max(minHeight,viewport.ClientSize.Height);
                    if(layout.Height!=desiredHeight)layout.Height=desiredHeight;
                    int w=mid.ClientSize.Width;if(w>mid.Panel1MinSize+mid.Panel2MinSize+mid.SplitterWidth){int d=Math.Max(mid.Panel1MinSize,Math.Min(w-mid.Panel2MinSize-mid.SplitterWidth,(int)(w*.58)));if(mid.SplitterDistance!=d)mid.SplitterDistance=d;}
                    w=low.ClientSize.Width;if(w>low.Panel1MinSize+low.Panel2MinSize+low.SplitterWidth){int d=Math.Max(low.Panel1MinSize,Math.Min(w-low.Panel2MinSize-low.SplitterWidth,(int)(w*.66)));if(low.SplitterDistance!=d)low.SplitterDistance=d;}
                }
                finally{resizingLayout=false;}
            };
            cards.SizeChanged+=delegate{resizeLayout();};head.SizeChanged+=delegate{resizeLayout();};mid.SizeChanged+=delegate{resizeLayout();};low.SizeChanged+=delegate{resizeLayout();};viewport.SizeChanged+=delegate{resizeLayout();};
            layout.Height=40+88+250+340+layout.Padding.Vertical;resizeLayout();
            tempCards.Controls.Add(new Label { Text = "按测点编号显示；5/6物理标签待确认", AutoSize = true, ForeColor = Color.FromArgb(122, 137, 153), Margin = new Padding(5, 6, 0, 0) });
        }
        Panel Section(string title, Control content)
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(9), Margin = new Padding(4) };
            TableLayoutPanel stack = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Color.White };
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label label = new Label { Dock = DockStyle.Fill, Text = title, Font = SectionFont, ForeColor = Color.FromArgb(29, 67, 107), TextAlign = ContentAlignment.MiddleLeft };
            content.Dock = DockStyle.Fill; stack.Controls.Add(label, 0, 0); stack.Controls.Add(content, 0, 1); panel.Controls.Add(stack); return panel;
        }
        Panel MetricCard(string title, out Label value)
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(3, 0, 4, 0), Padding = new Padding(9, 6, 7, 4) };
            TableLayoutPanel stack = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 }; stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 21)); stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stack.Controls.Add(new Label { Dock = DockStyle.Fill, Text = title, ForeColor = Color.FromArgb(94, 116, 140), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            value = new Label { Dock = DockStyle.Fill, Text = "—", Font = PageTitleFont, ForeColor = Color.FromArgb(36, 105, 171), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = false, AutoSize = false };
            stack.Controls.Add(value, 0, 1); panel.Controls.Add(stack); return panel;
        }
        internal static void CapturePreview(string requestedSize,int duration=6500)
        {
            Program.ConfigureApplicationStylesAndDpi();
            string[] dimensions = requestedSize.Split('x'); int width = 1360, height = 920;
            if (dimensions.Length == 2) { Int32.TryParse(dimensions[0], out width); Int32.TryParse(dimensions[1], out height); }
            width = Math.Max(1050, width); height = Math.Max(720, height);
            string previewBase=Environment.GetEnvironmentVariable("BMS_PREVIEW_ROOT");if(String.IsNullOrWhiteSpace(previewBase))previewBase=Path.Combine(Path.GetTempPath(),"BmsSerialDemo");
            string isolatedRoot=Path.Combine(previewBase,"preview-"+Guid.NewGuid().ToString("N").Substring(0,12));
            using (MainForm form = new MainForm(isolatedRoot,Path.Combine(isolatedRoot,"settings","recording-period.txt")))
            {
                // C2：截图/预览在临时隔离根中运行——模拟样本、分期状态、云端配置、采集间隔设置
                // 全部落在隔离目录，正式数据库与真实云端不受任何污染；结束后整目录清理。
                form.previewMode = false; form.save.Checked = false; form.ShowInTaskbar = false; form.Opacity = 0; form.WindowState = FormWindowState.Normal; form.Size = new Size(width, height);
                form.Shown += async delegate
                {
                    try
                    {
                        form.ToggleConnection(); DateTime sampleStart=DateTime.UtcNow.AddSeconds(-54);
                        for (int i = 0; i < 28; i++) form.trend.Add(1, i == 27 ? 52.92 : 52.9 + Math.Sin(i / 4.0) * .08, i == 27 ? -1.23 : -1.2 + Math.Cos(i / 5.0) * .2, i == 27 ? 80 : 79 + (int)Math.Round(Math.Sin(i / 7.0)), sampleStart.AddSeconds(i*2));
                        await form.Send(0x42, 255, new byte[] { 255 }); await form.Send(0x44, 255, new byte[] { 255 });form.TogglePolling();await Task.Delay(duration);form.TogglePolling();
                        string previewRoot=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-screenshots");Directory.CreateDirectory(previewRoot);string[] names={"实时总览","数据记录","云端连接","通信诊断"};string[] files={"live","storage","cloud","diagnostics"};for(int i=0;i<names.Length;i++){foreach(TabPage tab in form.mainTabs.TabPages)if(tab.Text==names[i])form.mainTabs.SelectedTab=tab;form.PerformLayout();Application.DoEvents();await Task.Delay(120);using(Bitmap bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(0,0,bmp.Width,bmp.Height));bmp.Save(Path.Combine(previewRoot,files[i]+"-"+width+"x"+height+".png"));}}
                    }
                    catch (Exception e) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview-error.txt"), e.ToString()); }
                    finally { form.Disconnect(); form.Close(); }
                };
                Application.Run(form);
            }
            try{if(Directory.Exists(isolatedRoot))Directory.Delete(isolatedRoot,true);}catch(Exception cleanupFailure){CrashLogger.Write("preview isolated root cleanup",cleanupFailure);}
        }
        internal static void Add(FlowLayoutPanel panel, string text, Control control)
        {
            int hostWidth=control.Width+16;
            TableLayoutPanel group=new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=2,RowCount=1,Margin=new Padding(3,1,4,1),Padding=Padding.Empty,BackColor=panel.BackColor};
            group.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));group.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label caption=new Label{Text=text,AutoSize=true,Margin=new Padding(3,0,4,0),TextAlign=ContentAlignment.MiddleLeft,Anchor=AnchorStyles.Left};
            RoundedInputHost host=new RoundedInputHost(control){Width=hostWidth,Height=36,Margin=new Padding(2,2,3,2)};
            group.Controls.Add(caption,0,0);group.Controls.Add(host,1,0);panel.Controls.Add(group);
        }
        SampleStore GetStoreForPage(string source)
        {
            SampleStore store;if(String.Equals(activeStore==null?null:activeStore.Source,source,StringComparison.OrdinalIgnoreCase))return activeStore;
            IList<string> found=monthlyCatalog.Discover(source);string db=found.Count>0?found[found.Count-1]:Path.Combine(dataRoot,source+".db");
            if(!File.Exists(db))throw new FileNotFoundException("数据库不存在",db);
            string key="query|"+source;if(stores.TryGetValue(key,out store)){if(String.Equals(store.DatabasePath,db,StringComparison.OrdinalIgnoreCase))return store;store.Dispose();stores.Remove(key);}
            store=new SampleStore(source,db,false,true);stores.Add(key,store);store.Ready.GetAwaiter().GetResult();return store;
        }
        void ClearDisplayedSource()
        {
            latestSnapshots.Clear();latestPacks=new PackData[0];freshness.Clear();alarmFreshness.Clear();alarmByPack.Clear();runtimeByPack.Clear();alarmSnapshotByPack.Clear();
            for(int p=1;p<=16;p++)trend.Clear(p);grid.Rows.Clear();rows.Clear();foreach(Label m in metrics)m.Text="—";spread.Text="";ShowEmptyCards("来源已切换，等待新数据");alarmList.Items.Clear();ReleaseRuntimeChips();
            if(cellSectionHeader!=null)cellSectionHeader.Text="电芯电压";connectionState.Text="● 来源已切换，等待新数据";
        }
        void RefreshPorts() { ports.Items.Clear(); ports.Items.AddRange(SerialPort.GetPortNames()); if (ports.Items.Count > 0) ports.SelectedIndex = 0; }
        void TogglePolling()
        {
            if (!connected) { Notice("请先连接设备"); return; }
            polling = !polling; poll.Text = polling ? "停止轮询" : "开始轮询";
            if (livePollButton != null) { livePollButton.Text = polling ? "停止实时采集" : "开始实时采集"; livePollButton.IconGlyph=polling?"Ⅱ":"▶"; }
            poll.Text = polling ? "停止轮询" : "开始轮询"; poll.IconGlyph=polling?"Ⅱ":"▶";
            if (polling) { recordingFailure=null;nextPollTicks=Stopwatch.GetTimestamp();timer.Start(); } else timer.Stop();
        }
        async void PeriodChanged()
        {
            if(!previewMode)try{Directory.CreateDirectory(Path.GetDirectoryName(periodSettingsPath));File.WriteAllText(periodSettingsPath,((int)period.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));}catch(Exception e){Notice("采集间隔无法保存："+e.Message);}
            if(polling)nextPollTicks=Stopwatch.GetTimestamp()+(long)(period.Value*Stopwatch.Frequency);
            if(activeStore!=null&&activeStore.GetStatus().IsRecording)try{await activeStore.RecordPolicyChangeAsync((int)period.Value);}catch(Exception e){Notice("采集间隔已更新，但策略元信息写入失败："+e.Message);}
        }
        void PollTick()
        {
            if(!polling||!connected)return;long now=Stopwatch.GetTimestamp();if(now<nextPollTicks)return;
            long interval=(long)period.Value*Stopwatch.Frequency;long slots=(now-nextPollTicks)/interval+1;nextPollTicks+=slots*interval;long skipped=slots-(pollCycleBusy?0:1);if(skipped>0)skippedRounds+=(int)Math.Min(Int32.MaxValue,skipped);
            if(pollCycleBusy)return;
            try{ApplyQueuedPartitionSettings();activeRound=++acquisitionRound;activeRoundPeriodSeconds=(int)period.Value;PrepareRoundPartition(simulatedConnection?"simulation":"serial",DateTime.UtcNow,activeRound);PollOnce();}
            catch(Exception e){if(polling)TogglePolling();Notice("无法开始本地记录分期："+e.Message);}
        }
        void Notice(string text) { status.Text = text; Log(text); }
        void SetConnectionState(string text,Color color){if(connectionState.Text!=text)connectionState.Text=text;if(connectionState.ForeColor!=color)connectionState.ForeColor=color;}
        void ToggleConnection()
        {
            if (connected) { Disconnect(); return; }
            try
            {
                simulatedConnection = simulate.Checked;
                cloudPublisher.ResetCaptureSession();
                if (!simulatedConnection)
                {
                    if (ports.SelectedItem == null) { Notice("没有可用串口，请连接 USB-RS485 转换器并刷新"); return; }
                    serial = new SerialPort(ports.Text, (int)baud.Value, Parity.None, 8, StopBits.One) { ReadTimeout = 500, WriteTimeout = 1000 };
                    serial.DataReceived += OnSerialData; serial.Open();
                }
                string chosenSource = simulatedConnection ? "simulation" : "serial";
                if(!String.Equals(displaySource,chosenSource,StringComparison.OrdinalIgnoreCase)){ClearDisplayedSource();displaySource=chosenSource;}
                if (!String.Equals(activeStore == null ? null : activeStore.Source, chosenSource, StringComparison.OrdinalIgnoreCase)) ClearDisplayedSource();
                connected = true; generation++; connect.Text = "断开"; simulate.Enabled = ports.Enabled = baud.Enabled = address.Enabled = false; freshnessTimer.Start();
                if(storagePage!=null)storagePage.SetActiveSource(chosenSource);
                if (save.Checked) try { OpenLog(); } catch (Exception logFailure) { save.Checked = false; CrashLogger.Write("raw log unavailable at connect", logFailure); Notice("原始日志不可用，已取消勾选；连接继续：" + logFailure.Message); }
                Notice(simulatedConnection ? "已连接模拟设备；开始实时采集后按设置的周期记录。" : "已连接 " + ports.Text + "，8N1；开始实时采集后记录。请关闭占用同一串口的旧 BMS Tool。");
            }
            catch (Exception e) { Disconnect(); Notice("连接失败：" + e.Message); }
        }
        void Disconnect()
        {
            if(cloudPublisher!=null)cloudPublisher.ResetCaptureSession();
            string recordError = null;
            connected = false; generation++; polling = false; timer.Stop(); freshnessTimer.Stop(); poll.Text = "开始轮询";poll.IconGlyph="▶";
            try { if (activeStore != null) activeStore.StopSessionAsync().GetAwaiter().GetResult(); }
            catch (Exception e) { CrashLogger.Write("storage session stop during shutdown",e);recordError = "本地记录关闭失败：" + e.Message; }
            recordShutdownError=recordError;
            lock (receiveLock) { if (pending != null) pending.Completion.TrySetCanceled(); pending = null; framer.Reset(); }
            if (serial != null) { SerialPort closingSerial=serial;serial=null;closingSerial.DataReceived-=OnSerialData;try{closingSerial.Close();}catch(Exception e){CrashLogger.Write("serial close during shutdown",e);recordError=AppendShutdownError(recordError,"串口关闭失败："+e.Message);}try{closingSerial.Dispose();}catch(Exception e){CrashLogger.Write("serial dispose during shutdown",e);recordError=AppendShutdownError(recordError,"串口资源释放失败："+e.Message);} }
            connect.Text = "连接"; simulate.Enabled = ports.Enabled = baud.Enabled = address.Enabled = true;
            if (writer != null) { StreamWriter closingWriter=writer;writer=null;try{closingWriter.Dispose();}catch(Exception e){CrashLogger.Write("raw log dispose during shutdown",e);recordError=AppendShutdownError(recordError,"通信日志关闭失败："+e.Message);} }
            recordShutdownError=recordError;
            connectionState.Text = "● 未连接 · 数据已过期"; connectionState.ForeColor = Color.Gray;
            if (livePollButton != null) { livePollButton.Text = "开始实时采集";livePollButton.IconGlyph="▶"; }
            status.Text = recordError ?? "未连接；实时页保留最后一次采样并标记为已过期。";
        }
        static string AppendShutdownError(string existing,string addition){return String.IsNullOrEmpty(existing)?addition:existing+"\r\n"+addition;}
        void OpenLog()
        {
            // C8：安装目录不可写时回退到用户目录（与 CrashLogger 一致）；两处都失败才抛出，由调用方决定降级。
            string primaryDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            string fallbackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BmsSerialDemo", "logs");
            Exception firstFailure = null;
            foreach (string dir in new[] { primaryDir, fallbackDir })
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                    writer = new StreamWriter(path, false, new UTF8Encoding(true)) { AutoFlush = true };
                    Log("日志文件：" + path + (String.Equals(dir, fallbackDir, StringComparison.OrdinalIgnoreCase) && !String.Equals(primaryDir, fallbackDir, StringComparison.OrdinalIgnoreCase) ? "（安装目录不可写，已改用用户目录）" : ""));
                    return;
                }
                catch (Exception e) { if (firstFailure == null) firstFailure = e; }
            }
            throw new IOException("原始日志目录不可用：" + (firstFailure == null ? "未知错误" : firstFailure.Message), firstFailure);
        }
        void Log(string text)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + text;
            if (log.TextLength > 120000) log.Text = log.Text.Substring(log.TextLength - 60000);
            log.AppendText(line + Environment.NewLine);
            if (save.Checked)
            {
                try { if (writer == null && connected) OpenLog(); if (writer != null) writer.WriteLine(line); }
                catch (Exception e) { save.Checked = false; status.Text = "日志写入失败：" + e.Message; if (writer != null) { try { writer.Dispose(); } catch { } writer = null; } }
            }
        }
        void Post(Action action) { if (closing || IsDisposed || !IsHandleCreated) return; try { BeginInvoke(action); } catch (InvalidOperationException) { } }
        void OnSerialData(object sender, SerialDataReceivedEventArgs args)
        {
            try
            {
                SerialPort port = (SerialPort)sender; byte[] bytes = new byte[port.BytesToRead]; int n = port.Read(bytes, 0, bytes.Length); if (n != bytes.Length) Array.Resize(ref bytes, n);
                if(rawByteDiagnostics)Post(delegate { Log("RX 字节 HEX=" + Protocol.Hex(bytes)); });
                lock (receiveLock) framer.Feed(bytes);
            }
            catch (Exception e) { Post(delegate { Notice("串口接收失败：" + e.Message); Disconnect(); }); }
        }
        void ReceiveFrame(byte[] raw)
        {
            try
            {
                Frame f = Protocol.Decode(raw);
                Post(delegate { Log("RX 完整帧 ASCII=" + Encoding.ASCII.GetString(raw).Replace("\r", "<CR>")); });
                if (pending != null && MatchesPending(f, pending)) pending.Completion.TrySetResult(f);
                else Post(delegate { Request waiting = pending; Log("未匹配当前请求的响应（地址/布局不符，疑似上一命令的迟到帧）ADR=" + f.Address + " RTN=0x" + f.ReturnCode.ToString("X2") + (waiting == null ? "，当前无待答请求" : "，待答 CMD=" + waiting.Command.ToString("X2") + " ADR=" + waiting.Address) + "，已保留原始帧"); });
            }
            catch (Exception e) { Post(delegate { Log("无效响应：" + e.Message + " HEX=" + Protocol.Hex(raw)); }); }
        }
        // 响应帧 CID2 是 RTN 而非命令回显（协议文档示例 ~52 01 46 00… 与 Simulator.Respond 均如此），
        // 无法按命令字匹配；对 42/44 轮询命令改用布局校验识别迟到响应，避免实时帧被当作告警解析入库（C9）。
        internal static bool MatchesPending(Frame f, Request req)
        {
            if (f.Address != req.Address) return false;
            if (req.Command == 0x42) { try { string layout; DataParser.Realtime(f.Info, req.Pack, out layout); return true; } catch (FormatException) { return false; } catch (ArgumentException) { return false; } }
            if (req.Command == 0x44) { try { DataParser.Alarm(f.Info, req.Pack); return true; } catch (FormatException) { return false; } catch (ArgumentException) { return false; } }
            return true;
        }
        byte SelectedPack { get { return all.Checked ? (byte)255 : (byte)pack.Value; } }
        async Task Send(byte cmd, byte p, byte[] info)
        {
            if (!connected) { Notice("请先连接"); return; }
            if (!await gate.WaitAsync(0)) { Notice("上一条请求尚未完成，本次跳过"); return; }
            int session = generation; Request req = new Request { Command = cmd, Pack = p, Address = (byte)address.Value, AcquisitionRound=polling&&(cmd==0x42||cmd==0x44)?activeRound:0, PeriodSeconds=polling&&(cmd==0x42||cmd==0x44)?(int?)activeRoundPeriodSeconds:null };
            try
            {
                int delay = (int)(quarantineUntil - DateTime.UtcNow).TotalMilliseconds;
                if (delay > 0) await Task.Delay(delay);
                if (!connected || session != generation) return;
                byte[] tx = Protocol.Encode(0x21, req.Address, cmd, info);
                requestStarted = DateTime.Now;
                lock (receiveLock) { framer.Reset(); pending = req; }
                Log("TX CMD=" + cmd.ToString("X2") + " ASCII=" + Encoding.ASCII.GetString(tx).Replace("\r", "<CR>") + " HEX=" + Protocol.Hex(tx));
                if (simulatedConnection)
                {
                    await Task.Delay(100);
                    if (!connected || session != generation) return;
                    byte[] rx = Simulator.Respond(req.Address, cmd, p);
                    Log("RX 模拟 HEX=" + Protocol.Hex(rx)); lock (receiveLock) { framer.Feed(Sub(rx, 0, 7)); framer.Feed(Sub(rx, 7, rx.Length - 7)); }
                }
                else serial.Write(tx, 0, tx.Length);
                Task winner = await Task.WhenAny(req.Completion.Task, Task.Delay((int)timeout.Value));
                if (winner != req.Completion.Task)
                {
                    pollFailure++; quarantineUntil = DateTime.UtcNow.AddMilliseconds((int)timeout.Value); Notice("响应超时，等待一个超时窗口后再发送；无法绝对排除迟到响应，请查看原始帧"); return;
                }
                Frame frame = await req.Completion.Task;
                if (session != generation) return;
                if (frame.ReturnCode != 0) { Notice("设备返回错误 RTN=0x" + frame.ReturnCode.ToString("X2")); return; }
                Display(frame, cmd, p, (int)Math.Max(0, (DateTime.Now - requestStarted).TotalMilliseconds),req.AcquisitionRound,req.PeriodSeconds);
            }
            catch (TaskCanceledException) { }
            catch (Exception e) { pollFailure++; Notice("读取失败：" + e.Message); }
            finally { lock (receiveLock) { if (pending == req) pending = null; } gate.Release(); }
        }
        static byte[] Sub(byte[] bytes, int start, int len) { byte[] result = new byte[len]; Array.Copy(bytes, start, result, 0, len); return result; }
        async void PollOnce()
        {
            if (pollCycleBusy || !connected || !polling) return;
            pollCycleBusy = true; int session = generation; byte p = SelectedPack;
            try { await Send(0x42, p, new[] { p }); if (connected && polling && session == generation) await Send(0x44, p, new[] { p }); }
            finally { pollCycleBusy = false; if(!closing)try{ApplyQueuedPartitionSettings();}catch(Exception e){Notice("分期设置未生效："+e.Message);} }
        }
        async Task ReadOnce()
        {
            byte[] cmds = { 0x42, 0x44, 0x4d, 0xe9, 0x47, 0x4b, 0x4c }; byte cmd = cmds[command.SelectedIndex], p = SelectedPack;
            if (p == 255 && cmd != 0x42 && cmd != 0x44) { Notice("此命令请选择单个 Pack"); return; }
            byte[] info = cmd == 0x4d ? new byte[0] : cmd == 0x4b || cmd == 0x4c ? new[] { (byte)historyAction.Value, p } : new[] { p };
            await Send(cmd, p, info);
        }
        void Display(Frame f, byte cmd, byte requested, int elapsed,long acquisitionRound=0,int? periodSeconds=null)
        {
            if (InvokeRequired) { Invoke((Action)delegate { Display(f, cmd, requested, elapsed,acquisitionRound,periodSeconds); }); return; }
            string header = "CMD=" + cmd.ToString("X2") + " ADR=" + f.Address + " VER=0x" + f.Version.ToString("X2") + (simulatedConnection ? " [模拟]" : "") + "\r\n";
            string raw = "\r\nINFO HEX=" + Protocol.Hex(f.Info);
            if (cmd == 0x42)
            {
                string layout; List<PackData> packs = DataParser.Realtime(f.Info, requested, out layout);
                StringBuilder s = new StringBuilder(header + layout + "\r\n");
                List<PackData> currentView = new List<PackData>();
                foreach (PackData p in packs)
                {
                    AlarmSnapshot observedAlarm=alarmSnapshotByPack.ContainsKey(p.Pack)?alarmSnapshotByPack[p.Pack]:null;RealtimeSnapshot snapshot = RealtimeSnapshot.Capture(f,p,simulatedConnection?"simulation":"serial",DateTime.UtcNow,null, acquisitionRound, periodSeconds,observedAlarm);
                    latestSnapshots[p.Pack]=snapshot; PackData displayData=snapshot.ToPackData(); currentView.Add(displayData);
                    int row; if (!rows.TryGetValue(p.Pack, out row)) { row = grid.Rows.Add(); rows[p.Pack] = row; }
                    grid.Rows[row].SetValues(p.Pack, DateTime.Now.ToString("HH:mm:ss"), displayData.Voltage, displayData.Current, displayData.Soc, displayData.Soh, displayData.RemainingAh, displayData.TotalAh, displayData.Cycles);
                    s.AppendLine("Pack " + p.Pack + " 电芯 mV：" + string.Join(", ", displayData.Cells));
                    s.AppendLine("温度 ℃：" + string.Join(", ", displayData.Temperatures));
                    s.AppendLine("均衡原始位1-16=0x" + displayData.BalanceLow.ToString("X4") + "，17-32=0x" + displayData.BalanceHigh.ToString("X4") + "（含义待实机确认） 湿度=" + displayData.Humidity + "%");
                    if(connected&&!previewMode&&acquisitionRound>0&&recordingFailure==null)try{string routed; if(roundDatabase.TryGetValue(acquisitionRound,out routed))ActivateStorePath(snapshot.Source,routed,acquisitionRound);else ActivateMonthlyStore(snapshot.Source,snapshot.ReceivedUtc,acquisitionRound);if(activeStore==null||!activeStore.TryEnqueue(snapshot))throw new IOException(activeStore==null?"没有活动数据库":activeStore.GetStatus().LastError);}catch(Exception ex){StopForRecordingFailure(ex);}
                    if(!diagnosticIsolation)publisher.Publish(snapshot);
                }
                List<PackData> cachedView=new List<PackData>();foreach(RealtimeSnapshot cached in latestSnapshots.Values)cachedView.Add(cached.ToPackData());latestPacks=cachedView.ToArray();
                details.Text = s + raw;
                pollSuccess++; pollElapsed = elapsed; lastUpdate = DateTime.Now;
                foreach (PackData item in packs) { freshness[item.Pack] = DateTime.Now; trend.Add(item.Pack, item.Voltage, item.Current, item.Soc, DateTime.UtcNow); }
                RenderPack();
            }
            else if (cmd == 0x44) { pollSuccess++; pollElapsed = elapsed; lastAlarmUpdate = DateTime.Now; string alarm = DataParser.Alarm(f.Info, requested); Log("告警读取成功：" + alarm.Replace("\r\n", " | ")); details.Text = header + alarm + raw; ShowAlarm(alarm); StoreAlarmFrames(f,requested,alarm,acquisitionRound,periodSeconds); }
            else if (cmd == 0x4d)
            {
                if (f.Info.Length != 7) throw new FormatException("时间字段不是 7 字节");
                Cursor c = new Cursor(f.Info, 0); DateTime dt = new DateTime(c.U16(), c.U8(), c.U8(), c.U8(), c.U8(), c.U8()); details.Text = header + "设备时间：" + dt.ToString("yyyy-MM-dd HH:mm:ss") + raw;
            }
            else if (cmd == 0xe9) details.Text = header + "序列号载荷（含 Pack 字段，NUL/FF 填充需按固件确认）：\r\n" + Encoding.ASCII.GetString(f.Info).Replace("\0", "<NUL>") + raw;
            else details.Text = header + "原始响应已接收并通过校验；参数和历史数据暂不做字段解码。\r\n历史动作需手动逐条发送，01 表示传输完成，具体按文档核对。" + raw;
            if (cmd == 0x42 || cmd == 0x44) SetConnectionState((simulatedConnection ? "● 模拟" : "● 已连接") + "  成功 " + pollSuccess + " / 失败 " + pollFailure + "  耗时 " + pollElapsed + " ms  42 " + (lastUpdate == DateTime.MinValue ? "—" : lastUpdate.ToString("HH:mm:ss")) + "  44 " + (lastAlarmUpdate == DateTime.MinValue ? "—" : lastAlarmUpdate.ToString("HH:mm:ss")),Color.FromArgb(20,130,110));
            status.Text = recordingFailure ?? ("最近成功：" + DateTime.Now.ToString("HH:mm:ss.fff") + " CMD=" + cmd.ToString("X2") + (simulatedConnection ? " [模拟数据]" : ""));
        }
        void Display(Frame f, byte cmd, byte requested) { Display(f, cmd, requested, 0); }
        void RenderPack()
        {
            if (latestPacks.Length == 0) return;
            int selected = viewPack.SelectedIndex + 1;
            PackData p = null; foreach (PackData item in latestPacks) if (item.Pack == selected) { p = item; break; }
            if (p == null) { foreach (Label m in metrics) m.Text = "—"; spread.Text=""; ShowEmptyCards("尚未收到 Pack " + selected + " 数据"); if (cellSectionHeader != null) cellSectionHeader.Text = "电芯电压"; trend.SelectedPack = selected; return; }
            metrics[0].Text = p.Voltage.ToString("0.00") + " V"; metrics[1].Text = p.Current.ToString("+0.00;-0.00;0.00") + " A";
            metrics[2].Text = p.Soc + "%"; metrics[3].Text = p.Soh + "%"; metrics[4].Text = p.RemainingAh.ToString("0.00") + " Ah"; metrics[5].Text = p.TotalAh.ToString("0.00") + " Ah"; metrics[6].Text = p.Cycles.ToString();
            EnsureCellCards(p.Cells.Length); int min = Int32.MaxValue, max = Int32.MinValue; for (int i = 0; i < p.Cells.Length; i++) { min = Math.Min(min, p.Cells[i]); max = Math.Max(max, p.Cells[i]); }
            for (int i = 0; i < p.Cells.Length; i++) { int mv = p.Cells[i]; Label tag=cellTagCache[i], value=cellValueCache[i]; tag.Text=(i+1).ToString("00")+"  "+(mv==min?"最低":mv==max?"最高":"电芯"); tag.ForeColor=mv==min?Color.FromArgb(185,111,30):mv==max?Color.FromArgb(45,111,181):Color.FromArgb(103,122,143); value.Text=mv+" mV"; cellBarCache[i].Value=max==min?500:200+(int)((mv-min)*800.0/(max-min)); }
            spread.Text = "最低 " + min + " mV   最高 " + max + " mV   压差 " + (max - min) + " mV";
            if (cellSectionHeader != null) cellSectionHeader.Text = "电芯电压 · " + spread.Text;
            EnsureTempCards(p.Temperatures.Length); for(int i=0;i<p.Temperatures.Length;i++)tempValueCache[i].Text=p.Temperatures[i]+" °C"; if(!tempCards.Controls.Contains(tempNote))tempCards.Controls.Add(tempNote);
            if (!cellsSection.Controls.Contains(spread)) { cellsSection.Controls.Add(spread); spread.Dock = DockStyle.Bottom; spread.BringToFront(); }
        }
        static Label EmptyLabel(string text) { return new Label { Text=text, AutoSize=true, ForeColor=Color.Gray, Margin=new Padding(8) }; }
        void ShowEmptyCards(string message){if(cellCardCache.Count>0)ReleaseCellCards();if(tempCardCache.Count>0)ReleaseTempCards();if(cellsEmpty==null)cellsEmpty=EmptyLabel(message);if(tempsEmpty==null)tempsEmpty=EmptyLabel(message);cellsEmpty.Text=message;tempsEmpty.Text=message;if(cellCards.Controls.Count!=1||!cellCards.Controls.Contains(cellsEmpty)){cellCards.Controls.Clear();cellCards.Controls.Add(cellsEmpty);}if(tempCards.Controls.Count!=1||!tempCards.Controls.Contains(tempsEmpty)){tempCards.Controls.Clear();tempCards.Controls.Add(tempsEmpty);}}
        void EnsureCellCards(int count)
        {
            if(cellCardCache.Count!=count){ReleaseCellCards();for(int i=0;i<count;i++){Panel c=new Panel{Width=98,Height=54,BackColor=Color.FromArgb(248,251,254),Margin=new Padding(3)};c.Paint+=delegate(object sender,PaintEventArgs e){using(Pen pen=new Pen(Color.FromArgb(220,231,242)))e.Graphics.DrawRectangle(pen,0,0,c.Width-1,c.Height-1);};TableLayoutPanel t=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty};t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));t.RowStyles.Add(new RowStyle(SizeType.Absolute,17));t.RowStyles.Add(new RowStyle(SizeType.Percent,100));t.RowStyles.Add(new RowStyle(SizeType.Absolute,5));Label tag=new Label{Dock=DockStyle.Fill,Font=cellTagFont,TextAlign=ContentAlignment.MiddleCenter};Label val=new Label{Dock=DockStyle.Fill,Font=cellValueFont,ForeColor=Color.FromArgb(35,66,98),TextAlign=ContentAlignment.MiddleCenter,AutoEllipsis=false};ProgressBar bar=new ProgressBar{Dock=DockStyle.Bottom,Height=5,Minimum=0,Maximum=1000,Style=ProgressBarStyle.Continuous};t.Controls.Add(tag,0,0);t.Controls.Add(val,0,1);t.Controls.Add(bar,0,2);c.Controls.Add(t);cellCardCache.Add(c);cellTagCache.Add(tag);cellValueCache.Add(val);cellBarCache.Add(bar);}}
            if(cellCards.Controls.Count!=count || (count>0&&!cellCards.Controls.Contains(cellCardCache[0]))) { cellCards.Controls.Clear();if(cellsEmpty!=null){cellsEmpty.Dispose();cellsEmpty=null;}foreach(Panel c in cellCardCache)cellCards.Controls.Add(c); }
        }
        void EnsureTempCards(int count)
        {
            if(tempCardCache.Count!=count){ReleaseTempCards();for(int i=0;i<count;i++){Panel c=new Panel{Width=78,Height=52,BackColor=Color.FromArgb(240,246,252),Margin=new Padding(3)};TableLayoutPanel t=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));t.RowStyles.Add(new RowStyle(SizeType.Absolute,17));t.RowStyles.Add(new RowStyle(SizeType.Percent,100));t.Controls.Add(new Label{Dock=DockStyle.Fill,Text="测点 "+(i+1),ForeColor=Color.FromArgb(103,122,143),Font=tempTagFont,TextAlign=ContentAlignment.MiddleCenter},0,0);Label value=new Label{Dock=DockStyle.Fill,Font=tempValueFont,ForeColor=Color.FromArgb(37,105,167),TextAlign=ContentAlignment.MiddleCenter};t.Controls.Add(value,0,1);c.Controls.Add(t);tempCardCache.Add(c);tempValueCache.Add(value);}}
            int expected=count+(tempCards.Controls.Contains(tempNote)?1:0);if(tempCards.Controls.Count!=expected || (count>0&&!tempCards.Controls.Contains(tempCardCache[0]))) { tempCards.Controls.Clear();if(tempsEmpty!=null){tempsEmpty.Dispose();tempsEmpty=null;}foreach(Panel c in tempCardCache)tempCards.Controls.Add(c);if(expected>count)tempCards.Controls.Add(tempNote); }
        }
        void DisposeChildren(Control parent){while(parent.Controls.Count>0){Control c=parent.Controls[0];parent.Controls.RemoveAt(0);c.Dispose();}}
        void ReleaseCellCards(){DisposeChildren(cellCards);cellsEmpty=null;cellCardCache.Clear();cellTagCache.Clear();cellValueCache.Clear();cellBarCache.Clear();}
        void ReleaseTempCards(){if(tempCards.Controls.Contains(tempNote))tempCards.Controls.Remove(tempNote);DisposeChildren(tempCards);tempsEmpty=null;tempCardCache.Clear();tempValueCache.Clear();}
        void ShowAlarm(string text)
        {
            int activePack = 1;
            foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Pack ")) { Int32.TryParse(line.Substring(5).Trim(), out activePack); alarmByPack[activePack] = new List<string>(); runtimeByPack[activePack] = new List<string>(); continue; }
                if (!alarmByPack.ContainsKey(activePack)) alarmByPack[activePack] = new List<string>();
                if (!runtimeByPack.ContainsKey(activePack)) runtimeByPack[activePack] = new List<string>();
                string s = line.Trim();
                if (s.Contains("低于下限") || s.Contains("高于上限") || s.Contains("其他错误")) alarmByPack[activePack].Add(s);
                int sc = s.IndexOf("短路=0x", StringComparison.Ordinal); if (sc >= 0 && sc + 7 <= s.Length && s.Substring(sc + 5, 2) != "00") alarmByPack[activePack].Add("短路保护状态：0x" + s.Substring(sc + 5, 2));
                alarmFreshness[activePack] = DateTime.Now;
                int pos = s.IndexOf("= 0x", StringComparison.Ordinal); if (s.StartsWith("状态") && pos > 0)
                {
                    int stateNo; byte bits;
                    if (Int32.TryParse(s.Substring(2, pos - 2).Trim(), out stateNo) && Byte.TryParse(s.Substring(pos + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out bits)) { DecodeStatus(activePack, stateNo, bits); DecodeRuntime(activePack, stateNo, bits); }
                }
            }
            RenderAlarm();
        }
        void StoreAlarmFrames(Frame frame, byte requested, string decoded,long acquisitionRound=0,int? periodSeconds=null)
        {
            if (!connected||previewMode||recordingFailure!=null||acquisitionRound<=0) return;
            int activePack = requested == 255 ? 0 : requested;
            foreach (string line in decoded.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("Pack ", StringComparison.Ordinal)) continue;
                if (!Int32.TryParse(line.Substring(5).Trim(), out activePack) || activePack <= 0) continue;
                AlarmSnapshot snapshot = AlarmSnapshot.Capture(frame, simulatedConnection ? "simulation" : "serial", activePack, DateTime.UtcNow, decoded,acquisitionRound,periodSeconds);alarmSnapshotByPack[activePack]=snapshot;
                try
                {
                    string routed;if(roundDatabase.TryGetValue(acquisitionRound,out routed))ActivateStorePath(snapshot.Source,routed,acquisitionRound);else ActivateMonthlyStore(snapshot.Source,snapshot.ReceivedUtc,acquisitionRound);
                    if(activeStore==null||!activeStore.TryEnqueue(snapshot))throw new IOException(activeStore==null?"没有活动数据库":activeStore.GetStatus().LastError);
                }
                catch(Exception ex){StopForRecordingFailure(ex);return;}
            }
        }
        void DecodeStatus(int p, int n, byte bits)
        {
            string[] names = null; string kind = n % 2 == 0 ? "保护" : "告警";
            if (n == 1) names = new[] { "单体过压", "单体欠压", "总压过压", "总压欠压", "充电过流", "放电过流1", "压差报警", "压差保护" };
            else if (n == 2) names = new[] { "单体过压保护", "单体欠压保护", "总压过压保护", "总压欠压保护", "充电过流保护", "放电过流1保护", "放电过流2保护", "短路保护" };
            else if (n == 3 || n == 4) names = new[] { "充电高温", "充电低温", "放电高温", "放电低温", "环境高温", "环境低温", "MOS高温", "MOS低温" };
            else if (n == 5) { names = new[] { "反接", "SOC低", "充电MOS故障", "放电MOS故障", "电芯过温", "电芯欠温", null, null }; kind = "状态"; }
            else if (n >= 8 && n <= 11) { names = new string[8]; for (int b = 0; b < 8; b++) names[b] = "电芯故障 " + ((n - 8) * 8 + b + 1); kind = "故障"; }
            else if (n == 14) { names = new[] { "放电MOS故障", "充电MOS故障", "前端芯片故障", "时钟故障", "数据存储故障", "参数存储故障", "高压保护", "预放MOS故障" }; kind = "故障/保护"; }
            else if (n == 15) { names = new[] { "P+绝缘保护", "P-绝缘保护", "电芯故障", "温度故障", "振动传感器故障", "限流板故障", null, null }; kind = "故障/保护"; }
            if (names == null) return;
            for (int b = 0; b < 8; b++) if ((bits & (1 << b)) != 0 && names[b] != null)
            { if (!alarmByPack.ContainsKey(p)) alarmByPack[p] = new List<string>(); alarmByPack[p].Add("状态" + n + " " + kind + "：" + names[b]); }
        }
        void RenderAlarm()
        {
            alarmList.Items.Clear(); int p = viewPack.SelectedIndex + 1; List<string> active;
            if (alarmByPack.TryGetValue(p, out active) && active.Count > 0) foreach (string item in active) alarmList.Items.Add(item);
            else if (alarmFreshness.ContainsKey(p)) alarmList.Items.Add("当前无已解码告警/保护");
            else alarmList.Items.Add("尚未读取告警 · 状态未知");
            List<string> run; if (!runtimeByPack.TryGetValue(p, out run)) run=new List<string>{"运行状态未采集"};EnsureRuntimeChips(run.Count);for(int i=0;i<run.Count;i++)SetRuntimeChip(runtimeChipCache[i],run[i]);
        }
        void DecodeRuntime(int p, int state, byte bits)
        {
            if (!runtimeByPack.ContainsKey(p)) runtimeByPack[p] = new List<string>();
            if (state == 5) { runtimeByPack[p].Add((bits & 0x40) != 0 ? "正在充电" : "未充电"); runtimeByPack[p].Add((bits & 0x80) != 0 ? "正在放电" : "未放电"); }
            else if (state == 6)
            {
                runtimeByPack[p].Add((bits & 0x01) != 0 ? "放电 MOS · 开" : "放电 MOS · 关");
                runtimeByPack[p].Add((bits & 0x02) != 0 ? "充电 MOS · 开" : "充电 MOS · 关");
                runtimeByPack[p].Add((bits & 0x08) != 0 ? "交流输入 · 有" : "交流输入 · 无");
                runtimeByPack[p].Add((bits & 0x20) != 0 ? "限流 · 启用" : "限流 · 未启用");
            }
        }
        Control RuntimeChip(string text)
        {
            bool on = text.Contains("正在充电") || text.Contains("正在放电") || text.Contains("· 开") || text.Contains("· 有") || (text.Contains("启用") && !text.Contains("未启用"));
            return new Label { Text = "● " + text, AutoSize = true, BackColor = on ? Color.FromArgb(232, 243, 252) : Color.FromArgb(244, 247, 250), ForeColor = on ? Color.FromArgb(42, 109, 168) : Color.FromArgb(120, 134, 149), Padding = new Padding(5, 4, 5, 4), Margin = new Padding(2) };
        }
        void EnsureRuntimeChips(int count){if(runtimeChipCache.Count==count)return;ReleaseRuntimeChips();for(int i=0;i<count;i++){Label chip=new Label{AutoSize=true,Padding=new Padding(5,4,5,4),Margin=new Padding(2)};runtimeChipCache.Add(chip);runtimeStates.Controls.Add(chip);}}
        void SetRuntimeChip(Label chip,string text){bool on=text.Contains("正在充电")||text.Contains("正在放电")||text.Contains("· 开")||text.Contains("· 有")||(text.Contains("启用")&&!text.Contains("未启用"));chip.Text="● "+text;chip.BackColor=on?Color.FromArgb(232,243,252):Color.FromArgb(244,247,250);chip.ForeColor=on?Color.FromArgb(42,109,168):Color.FromArgb(120,134,149);}
        void ReleaseRuntimeChips(){DisposeChildren(runtimeStates);runtimeChipCache.Clear();}
        void DrawAlarmItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground(); if (e.Index < 0) return;
            string text = alarmList.Items[e.Index].ToString(); bool inactive = text.Contains("当前无已解码");
            Color color = inactive ? Color.FromArgb(41, 129, 91) : text.Contains("未知") ? Color.FromArgb(139, 112, 67) : Color.FromArgb(184, 77, 61);
            using (Brush dot = new SolidBrush(color)) e.Graphics.FillEllipse(dot, e.Bounds.Left + 4, e.Bounds.Top + 8, 8, 8);
            using (Brush ink = new SolidBrush(Color.FromArgb(54, 72, 93))) e.Graphics.DrawString(text, Font, ink, e.Bounds.Left + 19, e.Bounds.Top + 4);
            e.DrawFocusRectangle();
        }
        void CheckFreshness()
        {
            if (!connected) return;
            long limit = Math.Max(2L * (long)period.Value * 1000L, (long)timeout.Value + 500L);
            int selected = viewPack.SelectedIndex + 1; DateTime value42, value44;
            bool has42 = freshness.TryGetValue(selected, out value42), has44 = alarmFreshness.TryGetValue(selected, out value44);
            string a = !has42 || (DateTime.Now - value42).TotalMilliseconds > limit ? "42已过期" : "42正常";
            string b = !has44 || (DateTime.Now - value44).TotalMilliseconds > limit ? "44已过期" : "44正常";
            string text=(simulatedConnection ? "● 模拟" : "● 已连接") + "  成功 " + pollSuccess + " / 失败 " + pollFailure + "  " + a + " · " + b;
            if(activeStore!=null){StoreStatus st=activeStore.GetStatus();text+="  入库 "+st.CommittedSamples+" / 待写 "+st.PendingCount;if(skippedRounds>0)text+=" / 跳过 "+skippedRounds;if(st.DiskWarning)text+="  磁盘可用空间低于1 GiB";if(!String.IsNullOrEmpty(st.LastError))text+="  记录失败："+st.LastError;}
            SetConnectionState(text,(a.Contains("过期") || b.Contains("过期")) ? Color.DarkOrange : Color.FromArgb(20,130,110));
        }
        internal static void SmokeTest()
        {
            using (MainForm form = new MainForm())
            {
                form.previewMode = true; form.save.Checked = false;
                IntPtr handle = form.Handle;
                form.ToggleConnection();
                Task task = form.Send(0x42, 1, new byte[] { 1 });
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                if (!task.IsCompleted || form.grid.Rows.Count != 1 || !form.details.Text.Contains("电芯 mV")) throw new Exception("模拟实时界面流程失败");
                task = form.Send(0x44, 1, new byte[] { 1 });
                deadline = DateTime.UtcNow.AddSeconds(5);
                while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                if (!task.IsCompleted || !form.details.Text.Contains("状态15") || form.runtimeStates.Controls.Count < 6 || !form.runtimeStates.Controls[2].Text.Contains("MOS")) throw new Exception("模拟告警/运行状态界面流程失败");
                form.Disconnect();
                if (form.connected || form.polling) throw new Exception("断开状态失败");
            }
        }
    }
}
