using System;
using System.Drawing;
using System.Windows.Forms;

using Acme.Cloud;
namespace BmsSerialDemo
{
    sealed class CloudPage:UserControl
    {
        // C26：静态共享字体——页面反复创建不再累积 GDI 句柄（进程生命周期内常量占用）。
        static readonly Font TitleFont=new Font("Microsoft YaHei UI",18,FontStyle.Bold),StateFont=new Font("Microsoft YaHei UI",13,FontStyle.Bold),StatusHeadingFont=new Font("Microsoft YaHei UI",12,FontStyle.Bold),CardGroupFont=new Font("Microsoft YaHei UI",9,FontStyle.Bold),BodyFont=new Font("Microsoft YaHei UI",9,FontStyle.Regular);
        readonly Func<CloudConfiguration> getConfig;readonly Action<CloudConfiguration> saveConfig;readonly CloudRealtimeService service;
        readonly TextBox endpoint=new TextBox{Width=440};readonly TextBox alias=new TextBox{Width=220};readonly TextBox token=new TextBox{Width=300,UseSystemPasswordChar=true};readonly StyledCheckBox enabled=new StyledCheckBox{Text="启用云端连接"};readonly StyledCheckBox includeSimulation=new StyledCheckBox{Text="允许上传模拟数据"};readonly StyledCheckBox clearToken=new StyledCheckBox{Text="删除已保存令牌"};
        readonly Label idValue=new Label{AutoSize=true,ForeColor=Color.FromArgb(46,95,145)};readonly Label state=new Label{AutoSize=true,Font=StateFont,ForeColor=Color.FromArgb(36,70,106)};readonly Label contact=new Label{AutoSize=true};readonly Label subscription=new Label{AutoSize=true};readonly Label upload=new Label{AutoSize=true};readonly Label sample=new Label{AutoSize=true};readonly Label counters=new Label{AutoSize=true};readonly Label detail=new Label{AutoSize=true,MaximumSize=new Size(760,0),ForeColor=Color.FromArgb(90,106,123)};readonly Label note=new Label{Text="云端按观看租约上传实时数据；网络中断不影响本地记录。令牌留空表示保留已保存令牌。",AutoSize=true,ForeColor=Color.FromArgb(112,126,142)};
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=1000};bool disposed;
        public CloudPage(Func<CloudConfiguration> config,Action<CloudConfiguration> save,CloudRealtimeService publisher)
        {
            AutoScaleMode=AutoScaleMode.Dpi;getConfig=config;saveConfig=save;service=publisher;BackColor=Color.FromArgb(239,244,249);Padding=new Padding(18);Font=BodyFont;
            FlowLayoutPanel root=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.LeftToRight,WrapContents=true,BackColor=BackColor,Padding=new Padding(6)};
            Label title=new Label{Text="云端连接",AutoSize=true,Font=TitleFont,ForeColor=Color.FromArgb(25,52,82),Margin=new Padding(2,0,0,12)};
            FlowLayoutPanel left=new FlowLayoutPanel{FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Margin=new Padding(0,0,12,8),Padding=Padding.Empty,BackColor=Color.Transparent};
            GroupBox identity=Card("设备身份");TableLayoutPanel identityGrid=FieldGrid();identityGrid.RowCount=2;AddField(identityGrid,"设备编号",idValue,0);AddField(identityGrid,"设备别名",new RoundedInputHost(alias){Height=36,Dock=DockStyle.Fill},1);identity.Controls.Add(identityGrid);
            GroupBox settings=Card("连接设置");TableLayoutPanel form=FieldGrid();form.RowCount=6;form.Controls.Add(enabled,0,0);form.SetColumnSpan(enabled,2);AddField(form,"服务地址",new RoundedInputHost(endpoint){Height=36,Dock=DockStyle.Fill},1);AddField(form,"设备令牌",new RoundedInputHost(token){Height=36,Dock=DockStyle.Fill},2);form.Controls.Add(clearToken,0,3);form.SetColumnSpan(clearToken,2);form.Controls.Add(includeSimulation,0,4);form.SetColumnSpan(includeSimulation,2);StyledActionButton saveButton=new StyledActionButton{Text="保存连接设置",IconGlyph="✓",Width=172,Height=36,BackColor=Color.FromArgb(65,111,232),ForeColor=Color.White,Margin=new Padding(4,6,4,2)};FlowLayoutPanel actions=new FlowLayoutPanel{AutoSize=true,WrapContents=true,Dock=DockStyle.Fill};StyledActionButton diagnoseButton=new StyledActionButton{Text="连接诊断",IconGlyph="?",Width=144,Height=36,Margin=new Padding(4,6,4,2)};actions.Controls.Add(saveButton);actions.Controls.Add(diagnoseButton);form.Controls.Add(actions,1,5);settings.Controls.Add(form);diagnoseButton.Click+=delegate{using(CloudDiagnosticsDialog dialog=new CloudDiagnosticsDialog(getConfig()))dialog.ShowDialog(this);};
            GroupBox credentials=Card("凭据备份");credentials.Height=126;
            TableLayoutPanel credentialLayout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=2,BackColor=Color.White,Padding=new Padding(6,5,6,8)};
            FlowLayoutPanel credentialActions=new FlowLayoutPanel{AutoSize=true,WrapContents=true,Dock=DockStyle.Fill};
            StyledActionButton exportButton=new StyledActionButton{Text="导出凭据",IconGlyph="↓",Width=132,Height=36,Margin=new Padding(4,6,4,2)};
            StyledActionButton importButton=new StyledActionButton{Text="导入凭据",IconGlyph="↑",Width=132,Height=36,Margin=new Padding(4,6,4,2)};
            credentialActions.Controls.Add(exportButton);credentialActions.Controls.Add(importButton);
            Label credentialHint=new Label{Text="换机器或重装系统时用：导出后凭同一口令在另一台机器导入，设备编号不变、无需重新注册。",AutoSize=true,MaximumSize=new Size(660,0),ForeColor=Color.FromArgb(100,116,135),Font=BodyFont,Margin=new Padding(8,2,8,6)};
            credentialLayout.Controls.Add(credentialActions,0,0);credentialLayout.Controls.Add(credentialHint,0,1);credentials.Controls.Add(credentialLayout);
            exportButton.Click+=delegate{ExportCredentials();};importButton.Click+=delegate{ImportCredentials();};
            GroupBox statusCard=Card("连接状态");TableLayoutPanel statusLayout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,RowCount=7,Padding=new Padding(8),BackColor=Color.White};statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,156));statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));AddStatus(statusLayout,"当前状态",state,0);AddStatus(statusLayout,"最后成功联系",contact,1);AddStatus(statusLayout,"观看订阅",subscription,2);AddStatus(statusLayout,"最后成功上传",upload,3);AddStatus(statusLayout,"最后上传采样",sample,4);AddStatus(statusLayout,"发送 / 失败 / 合并",counters,5);statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));detail.AutoSize=true;detail.MaximumSize=new Size(420,0);detail.Font=Font;statusLayout.Controls.Add(detail,0,6);statusLayout.SetColumnSpan(detail,2);statusCard.Controls.Add(statusLayout);
            left.Controls.Add(title);left.Controls.Add(identity);left.Controls.Add(settings);left.Controls.Add(credentials);note.MaximumSize=new Size(720,0);note.Font=Font;note.Margin=new Padding(4,4,4,10);left.Controls.Add(note);root.Controls.Add(left);root.Controls.Add(statusCard);Controls.Add(root);
            Action resize=delegate{int width=Math.Max(320,root.ClientSize.Width-28);bool wide=width>=1120;int leftWidth=wide?(int)(width*.59):width;left.Width=leftWidth;identity.Width=leftWidth;settings.Width=leftWidth;note.MaximumSize=new Size(leftWidth-18,0);statusCard.Width=wide?width-leftWidth-12:width;statusCard.Height=wide?Math.Max(410,root.ClientSize.Height-32):Math.Max(370,statusLayout.PreferredSize.Height+42);statusLayout.Width=statusCard.ClientSize.Width-24;detail.MaximumSize=new Size(Math.Max(180,statusCard.ClientSize.Width-42),0);form.ColumnStyles[1].Width=Math.Max(100,form.ClientSize.Width-170);};
            root.SizeChanged+=delegate{resize();};SizeChanged+=delegate{resize();};resize();UiTheme.Apply(this);
            saveButton.Click+=delegate{Save();};LoadConfig();timer.Tick+=delegate{RefreshStatus();};VisibleChanged+=delegate{if(Visible){RefreshStatus();timer.Start();}else timer.Stop();};Disposed+=delegate{disposed=true;service.StatusChanged-=ServiceStatusChanged;timer.Stop();timer.Dispose();};service.StatusChanged+=ServiceStatusChanged;
        }
        void ExportCredentials()
        {
            CloudConfiguration current=getConfig();
            using(SaveFileDialog dialog=new SaveFileDialog{Filter="设备凭据 (*.acmedevice)|*.acmedevice",FileName="BmsSerialDemo-"+current.DeviceId+".acmedevice",OverwritePrompt=true})
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                string password=PromptPassword("导出设备凭据","设置一个至少 8 位的口令。文件里只有口令才能解开，请单独保管——忘记口令只能重新签发令牌。");
                if(password==null)return;
                try{CloudCredentialBundle.ExportToFile(current,password,dialog.FileName);MessageBox.Show(this,"凭据已导出：\r\n"+dialog.FileName+"\r\n\r\n口令不会写进文件，请自行保管。","导出凭据",MessageBoxButtons.OK,MessageBoxIcon.Information);}
                catch(Exception e){MessageBox.Show(this,"导出失败："+e.Message,"导出凭据",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            }
        }
        void ImportCredentials()
        {
            using(OpenFileDialog dialog=new OpenFileDialog{Filter="设备凭据 (*.acmedevice)|*.acmedevice|所有文件 (*.*)|*.*"})
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                string password=PromptPassword("导入设备凭据","输入导出时设置的口令。");
                if(password==null)return;
                try
                {
                    CloudConfiguration imported=CloudCredentialBundle.ImportFromFile(dialog.FileName,password,getConfig().SavePath);
                    // 身份写回稳定位置、凭据落盘，再切换运行态：任一步失败都不会留下半截状态。
                    CloudIdentity.WriteMachineDeviceId("BmsSerialDemo",imported.DeviceId);
                    imported.Save();
                    saveConfig(imported);
                    token.Clear();idValue.Text=imported.DeviceId;RefreshStatus();
                    MessageBox.Show(this,"凭据已导入。设备编号："+imported.DeviceId+"\r\n\r\n本地采集数据目录按设备编号区分，重启本程序后生效。","导入凭据",MessageBoxButtons.OK,MessageBoxIcon.Information);
                }
                catch(Exception e){MessageBox.Show(this,"导入失败："+e.Message,"导入凭据",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            }
        }
        /// <summary>口令输入框。凭据唯一的保护就是它，所以用密码字符并禁止空提交。</summary>
        string PromptPassword(string title,string hint)
        {
            using(Form form=new Form{Text=title,Width=460,Height=200,FormBorderStyle=FormBorderStyle.FixedDialog,StartPosition=FormStartPosition.CenterParent,MaximizeBox=false,MinimizeBox=false,ShowInTaskbar=false,BackColor=Color.White,Font=BodyFont})
            {
                Label label=new Label{Text=hint,Left=14,Top=12,Width=420,Height=52,ForeColor=Color.FromArgb(88,105,125)};
                TextBox box=new TextBox{Left=14,Top=70,Width=420,UseSystemPasswordChar=true,Font=BodyFont};
                Button ok=new Button{Text="确定",Left=272,Top=112,Width=76,Height=30,DialogResult=DialogResult.OK};
                Button cancel=new Button{Text="取消",Left=358,Top=112,Width=76,Height=30,DialogResult=DialogResult.Cancel};
                form.Controls.Add(label);form.Controls.Add(box);form.Controls.Add(ok);form.Controls.Add(cancel);
                form.AcceptButton=ok;form.CancelButton=cancel;
                if(form.ShowDialog(this)!=DialogResult.OK)return null;
                return box.Text;
            }
        }
        static GroupBox Card(string heading){return new GroupBox{Text=heading,Width=720,Height=heading=="连接状态"?410:heading=="连接设置"?342:132,BackColor=Color.White,Padding=new Padding(10,14,10,8),Margin=new Padding(0,0,0,10),ForeColor=Color.FromArgb(34,70,108),Font=CardGroupFont};}
        static TableLayoutPanel FieldGrid(){TableLayoutPanel p=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,BackColor=Color.White,Padding=new Padding(6,5,6,8),Font=BodyFont};p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,160));p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));return p;}
        static void AddField(TableLayoutPanel panel,string caption,Control field,int row){panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));Label label=new Label{Text=caption,AutoSize=true,Margin=new Padding(4,9,8,4),ForeColor=Color.FromArgb(88,105,125),Font=BodyFont};field.Margin=new Padding(3,3,4,3);panel.Controls.Add(label,0,row);panel.Controls.Add(field,1,row);}
        static void AddStatus(TableLayoutPanel panel,string heading,Control value,int row){panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));Label label=new Label{Text=heading,AutoSize=true,Margin=new Padding(4,7,6,6),ForeColor=Color.FromArgb(100,116,135),Font=BodyFont};value.Margin=new Padding(4,7,4,6);value.Font=heading=="当前状态"?StatusHeadingFont:BodyFont;panel.Controls.Add(label,0,row);panel.Controls.Add(value,1,row);}
        void LoadConfig(){CloudConfiguration c=getConfig();idValue.Text=c.DeviceId;alias.Text=c.Alias;endpoint.Text=c.Endpoint;enabled.Checked=c.Enabled;includeSimulation.Checked=c.IncludeSimulation;token.Text="";token.AccessibleDescription="留空表示保留已保存令牌";RefreshStatus();}
        void Save(){CloudConfiguration old=getConfig(),c=new CloudConfiguration{DeviceId=old.DeviceId,Alias=alias.Text.Trim(),Endpoint=endpoint.Text.Trim(),Enabled=enabled.Checked,IncludeSimulation=includeSimulation.Checked,Token=clearToken.Checked?"":String.IsNullOrEmpty(token.Text)?old.Token:token.Text.Trim(),SavePath=old.SavePath};if(c.Enabled&&!c.HasValidEndpoint){MessageBox.Show(this,"启用连接时必须填写有效的 HTTPS 服务地址。","连接设置",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}if(c.Enabled&&String.IsNullOrWhiteSpace(c.Token)){MessageBox.Show(this,"启用连接时必须提供设备令牌。","连接设置",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}try{c.Save();saveConfig(c);token.Clear();clearToken.Checked=false;RefreshStatus();MessageBox.Show(this,"设置已保存在当前用户配置中；令牌由 Windows DPAPI 保护。连接结果请查看右侧状态；连接失败可运行连接诊断。","设置已保存",MessageBoxButtons.OK,MessageBoxIcon.Information);}catch(Exception e){MessageBox.Show(this,"设置未保存："+e.Message,"连接设置",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
        void ServiceStatusChanged(object sender,EventArgs e){if(IsDisposed||!IsHandleCreated||!Visible)return;try{BeginInvoke((Action)RefreshStatus);}catch{}}
        void RefreshStatus(){if(disposed)return;CloudRealtimeStatus s=service.Status;state.Text=s.State;contact.Text=s.LastContactUtc.HasValue?s.LastContactUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"尚无成功联系";subscription.Text=s.HasSubscription?"有效观看租约":"无有效租约";upload.Text=s.LastUploadUtc.HasValue?s.LastUploadUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"尚无上传";sample.Text=s.LastSampleUtc.HasValue?s.LastSampleUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"尚无上传样本";counters.Text=s.Sent+" / "+s.Failed+" / "+s.Merged;detail.Text=s.Detail;}
    }
}
