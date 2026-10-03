using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BmsSerialDemo
{
    sealed class CloudDiagnosticsDialog : Form
    {
        readonly CancellationTokenSource cancel = new CancellationTokenSource();
        readonly CloudConfiguration configuration;
        readonly TextBox report = new TextBox { Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Both, WordWrap=false, Dock=DockStyle.Fill };
        readonly Button save = new Button { Text="保存报告…", AutoSize=true, Enabled=false };
        readonly Button stop = new Button { Text="取消诊断", AutoSize=true };
        readonly Label hint = new Label { AutoSize=true, MaximumSize=new Size(850,0), Text="检查已保存的连接设置。仅发送一次认证心跳，不上传实验样本；本地采集和记录继续运行。" };
        string completed;
        bool closed;
        public CloudDiagnosticsDialog(CloudConfiguration source)
        {
            configuration = source==null?null:new CloudConfiguration { Enabled=source.Enabled, IncludeSimulation=source.IncludeSimulation, Endpoint=source.Endpoint, Token=source.Token, DeviceId=source.DeviceId, Alias=source.Alias, SavePath=source.SavePath, SavedTokenUnreadable=source.SavedTokenUnreadable };
            Text="云端连接诊断"; Width=930; Height=640; MinimumSize=new Size(640,440); StartPosition=FormStartPosition.CenterParent; AutoScaleMode=AutoScaleMode.Dpi;
            Font=SystemFonts.MessageBoxFont; Padding=new Padding(14);
            TableLayoutPanel layout=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            hint.Margin=new Padding(0,0,0,12);report.Margin=new Padding(0);report.Text="正在检查，请等待（通常不超过 40 秒）…";
            FlowLayoutPanel actions=new FlowLayoutPanel { Dock=DockStyle.Fill, AutoSize=true, FlowDirection=FlowDirection.RightToLeft, Padding=new Padding(0,10,0,0) };
            actions.Controls.Add(stop);actions.Controls.Add(save);layout.Controls.Add(hint,0,0);layout.Controls.Add(report,0,1);layout.Controls.Add(actions,0,2);Controls.Add(layout);
            stop.Click+=delegate { if(completed==null){cancel.Cancel();stop.Enabled=false;}else Close(); };
            save.Click+=delegate { using(SaveFileDialog dialog=new SaveFileDialog { Filter="诊断报告 (*.txt)|*.txt", FileName="BMS-cloud-diagnostic-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".txt", AddExtension=true, DefaultExt="txt" }) { if(dialog.ShowDialog(this)!=DialogResult.OK)return;try {File.WriteAllText(dialog.FileName,completed,new UTF8Encoding(true));}catch(Exception e){MessageBox.Show(this,"报告未保存："+CloudNetworkErrors.Describe(e),"保存报告",MessageBoxButtons.OK,MessageBoxIcon.Warning);} } };
            Shown+=delegate { StartDiagnosis(); };
            FormClosing+=delegate {closed=true;if(completed==null)cancel.Cancel();else cancel.Dispose();};
        }
        async void StartDiagnosis()
        {
            string result;
            try { result=await CloudDiagnostics.RunAsync(configuration,cancel.Token); }
            catch(Exception e) {result="诊断未完成："+CloudNetworkErrors.Describe(e);}
            if(closed||IsDisposed){cancel.Dispose();return;}
            completed=result;
            string savedPath=SaveAutomatic(result);
            report.Text=result;
            hint.Text="诊断完成。报告不含设备令牌或实验数据。"+(savedPath==null?"自动保存失败，请点击保存报告。":"已保存："+savedPath);
            save.Enabled=true;stop.Enabled=true;stop.Text="关闭";
        }
        static string SaveAutomatic(string text)
        {
            string name="cloud-diagnostic-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".txt";
            string[] roots={Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BmsSerialDemo","logs")};
            foreach(string root in roots)try{Directory.CreateDirectory(root);string path=Path.Combine(root,name);File.WriteAllText(path,text,new UTF8Encoding(true));return path;}catch{}
            return null;
        }
    }
}
