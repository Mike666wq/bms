using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BmsSerialDemo
{
    sealed class StoragePage : UserControl
    {
        readonly Func<string,SampleStore> getStore;
        readonly MonthlyCatalog catalog;
        readonly DataGridView grid = new DataGridView { Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,EnableHeadersVisualStyles=false,ColumnHeadersDefaultCellStyle={BackColor=Color.FromArgb(232,241,250),ForeColor=Color.FromArgb(29,58,89),Font=new Font("Microsoft YaHei UI",9,FontStyle.Bold)},DefaultCellStyle={SelectionBackColor=Color.FromArgb(211,231,250),SelectionForeColor=Color.FromArgb(25,52,82)} };
        readonly DataGridView libraries = new DataGridView { Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,EnableHeadersVisualStyles=false,ColumnHeadersDefaultCellStyle={BackColor=Color.FromArgb(232,241,250),ForeColor=Color.FromArgb(29,58,89),Font=new Font("Microsoft YaHei UI",9,FontStyle.Bold)} };
        readonly Label title=new Label{Text="本地数据记录",Dock=DockStyle.Fill,Font=new Font("Microsoft YaHei UI",15,FontStyle.Bold),ForeColor=Color.FromArgb(25,52,82),TextAlign=ContentAlignment.MiddleLeft};
        readonly Label summary=new Label{Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleRight,ForeColor=Color.FromArgb(75,96,120),Padding=new Padding(4,0,8,0)};
        readonly Label technicalStatus=new Label{Dock=DockStyle.Fill,AutoEllipsis=true,ForeColor=Color.FromArgb(94,112,132),Padding=new Padding(8,4,8,4),BackColor=Color.White};
        readonly Label inventorySummary=new Label{Dock=DockStyle.Fill,AutoEllipsis=true,ForeColor=Color.FromArgb(75,96,120),TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(4,0,4,0)};
        readonly DateTimePicker from = new DateTimePicker { Dock=DockStyle.Fill,Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm:ss",Value=DateTime.Today,Margin=new Padding(4,1,4,1) };
        readonly DateTimePicker to = new DateTimePicker { Dock=DockStyle.Fill,Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm:ss",Value=DateTime.Now,Margin=new Padding(4,1,4,1) };
        readonly NumericUpDown pack = new NumericUpDown { Minimum=0,Maximum=16,Value=0,Dock=DockStyle.Left,Width=70,Margin=new Padding(4,1,4,1) };
        readonly ComboBox source = new ComboBox { Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,FormattingEnabled=true,Margin=new Padding(4,1,4,1) };
        readonly StyledActionButton query=new StyledActionButton{Text="查询记录",IconGlyph="⌕",Dock=DockStyle.Fill,Margin=new Padding(4,1,4,1),BackColor=Color.FromArgb(65,111,232),ForeColor=Color.White};
        readonly StyledActionButton export=new StyledActionButton{Text="导出数据",IconGlyph="↓",Width=132,Height=36,Margin=new Padding(6,2,8,2),BackColor=Color.FromArgb(65,111,232),ForeColor=Color.White};
        readonly StyledActionButton cancelExport=new StyledActionButton{Text="取消",Width=76,Height=36,Visible=false,Margin=new Padding(2,4,8,2)};
        readonly ContextMenuStrip exportMenu=new ContextMenuStrip();
        readonly StyledActionButton followButton=new StyledActionButton{Text="回到最新",Width=104,Height=36,Margin=new Padding(4,3,5,3)};
        readonly StyledActionButton nextPage = new StyledActionButton { Text="更早 200 条",Width=120,Height=36,Enabled=false,Margin=new Padding(2,3,6,3) };
        readonly LinkLabel detailsToggle=new LinkLabel{Text="存储详情  ▸",AutoSize=false,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,LinkColor=Color.FromArgb(45,108,175),ActiveLinkColor=Color.FromArgb(35,82,136),Padding=new Padding(8,0,0,0)};
        readonly LinkLabel inventoryToggle=new LinkLabel{Text="数据库清单  ▸",AutoSize=false,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,LinkColor=Color.FromArgb(45,108,175),Padding=new Padding(6,0,0,0)};
        readonly ProgressBar exportProgress=new ProgressBar{Width=110,Height=16,Visible=false,Margin=new Padding(2,7,4,2)};
        readonly Label exportStatus=new Label{AutoSize=true,ForeColor=Color.FromArgb(69,105,150),Visible=false,Margin=new Padding(2,8,4,0)};
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval=1000 };
        readonly TableLayoutPanel layout;
        Panel headerPanel;
        CancellationTokenSource exportCancellation;
        long beforeId,lastFollowTotal=-1; StoredSample beforeSample; string lastFollowSource; bool following=true,queryBusy,disposed,settingSource,explicitSource,suppressFilter,queryPending,pendingLatest=true,entered,showDetails,showInventory; int queryGeneration; string activeSource,queryError;

        public StoragePage(Func<string,SampleStore> getStore,MonthlyCatalog catalog)
        {
            AutoScaleMode=AutoScaleMode.Dpi;this.getStore=getStore;this.catalog=catalog;BackColor=Color.FromArgb(239,244,249);Padding=new Padding(10);
            source.Items.AddRange(new object[]{"simulation","serial"});source.SelectedIndex=0;
            source.Format+=delegate(object sender,ListControlConvertEventArgs e){string value=e.ListItem as string;e.Value=value=="simulation"?"模拟设备":value=="serial"?"真实设备":e.ListItem;};
            Panel header=new Panel{Dock=DockStyle.Fill,BackColor=Color.Transparent,Margin=Padding.Empty};headerPanel=header;title.Dock=DockStyle.Left;title.Width=300;export.Anchor=AnchorStyles.Top|AnchorStyles.Right;cancelExport.Anchor=AnchorStyles.Top|AnchorStyles.Right;exportStatus.Anchor=AnchorStyles.Top|AnchorStyles.Right;exportProgress.Anchor=AnchorStyles.Top|AnchorStyles.Right;header.Controls.Add(exportProgress);header.Controls.Add(exportStatus);header.Controls.Add(cancelExport);header.Controls.Add(export);header.Controls.Add(title);header.Resize+=delegate{PositionHeaderActions();};
            TableLayoutPanel filters=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=5,RowCount=2,Padding=new Padding(4,2,4,2),BackColor=Color.White,CellBorderStyle=TableLayoutPanelCellBorderStyle.None,Margin=new Padding(0,3,0,3)};
            float[] widths={13,25,25,13,24};for(int i=0;i<widths.Length;i++)filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,widths[i]));filters.RowStyles.Add(new RowStyle(SizeType.Absolute,20));filters.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            AddFilter(filters,"数据源",source,0);AddFilter(filters,"开始时间",from,1);AddFilter(filters,"结束时间（不含）",to,2);AddFilter(filters,"Pack（0=全部）",pack,3);filters.Controls.Add(query,4,1);
            FlowLayoutPanel pager=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,AutoScroll=true,BackColor=Color.White,Margin=Padding.Empty,Padding=new Padding(3,0,2,0)};pager.Controls.Add(followButton);pager.Controls.Add(nextPage);
            Panel resultRow=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(3,0,2,0)};pager.Width=270;pager.Dock=DockStyle.Left;summary.Dock=DockStyle.Fill;resultRow.Controls.Add(summary);resultRow.Controls.Add(pager);
            Panel detailsPanel=new Panel{Dock=DockStyle.Fill,BackColor=Color.White};detailsPanel.Controls.Add(technicalStatus);
            Panel inventoryPanel=new Panel{Dock=DockStyle.Fill,BackColor=Color.White};inventoryPanel.Controls.Add(libraries);
            TableLayoutPanel inventoryHeader=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,BackColor=Color.White,Padding=new Padding(2,0,4,0)};inventoryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));inventoryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));inventoryHeader.Controls.Add(inventoryToggle,0,0);inventoryHeader.Controls.Add(inventorySummary,1,0);
            layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=8,BackColor=BackColor,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,62));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,25));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,0));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,30));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,0));
            layout.Controls.Add(header,0,0);layout.Controls.Add(filters,0,1);layout.Controls.Add(resultRow,0,2);layout.Controls.Add(detailsToggle,0,3);layout.Controls.Add(detailsPanel,0,4);layout.Controls.Add(grid,0,5);layout.Controls.Add(inventoryHeader,0,6);layout.Controls.Add(inventoryPanel,0,7);Controls.Add(layout);
            string[] columns={"ID","数据库","采集时间（本地）","地址","Pack","总压 V","电流 A","SOC %","SOH %","剩余 Ah","总容量 Ah","循环","电芯 mV（按编号）","温度 ℃（按测点）"};foreach(string c in columns)grid.Columns.Add(c,c);
            grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;grid.ScrollBars=ScrollBars.Both;grid.ColumnHeadersHeight=54;grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.DisableResizing;grid.ColumnHeadersDefaultCellStyle.WrapMode=DataGridViewTriState.True;grid.ColumnHeadersDefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleLeft;grid.RowTemplate.Height=30;
            int[] minimumWidths={52,112,230,50,54,76,78,62,62,76,80,62,180,150};for(int i=0;i<minimumWidths.Length;i++){grid.Columns[i].MinimumWidth=minimumWidths[i];grid.Columns[i].FillWeight=minimumWidths[i];grid.Columns[i].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;}
            string[] libraryColumns={"数据源","分期","数据库文件","大小 MiB","类型"};foreach(string c in libraryColumns)libraries.Columns.Add(c,c);libraries.Columns[0].FillWeight=55;libraries.Columns[1].FillWeight=55;libraries.Columns[2].FillWeight=180;libraries.Columns[3].FillWeight=65;libraries.Columns[4].FillWeight=55;
            exportMenu.Items.Add("导出为 CSV…",null,async delegate{await Export();});exportMenu.Items.Add("导出为 Excel 工作簿…",null,async delegate{await ExportXlsx();});
            export.Click+=delegate{exportMenu.Show(export,new Point(0,export.Height));};cancelExport.Click+=delegate{if(exportCancellation!=null)exportCancellation.Cancel();};query.Click+=async delegate{following=false;beforeId=0;nextPage.Enabled=false;await Query(true);};followButton.Click+=delegate{following=true;beforeId=0;SetToNow();Query(true);};nextPage.Click+=async delegate{following=false;await Query(false);};
            detailsToggle.LinkClicked+=delegate{showDetails=!showDetails;layout.RowStyles[4].Height=showDetails?58:0;detailsToggle.Text=showDetails?"存储详情  ▾":"存储详情  ▸";};inventoryToggle.LinkClicked+=delegate{showInventory=!showInventory;layout.RowStyles[7].Height=showInventory?155:0;inventoryPanel.Visible=showInventory;inventoryToggle.Text=showInventory?"数据库清单  ▾":"数据库清单  ▸";if(showInventory)RefreshInventory();};
            source.SelectedIndexChanged+=delegate{if(disposed)return;EventHandler sourceChanged=SelectedSourceChanged;if(sourceChanged!=null)sourceChanged(this,EventArgs.Empty);if(!settingSource)explicitSource=true;following=false;beforeId=0;lastFollowTotal=-1;lastFollowSource=null;queryGeneration++;nextPage.Enabled=false;grid.Rows.Clear();RefreshStatus();if(Visible)Query(true);};
            DateTimePicker[] dates={from,to};foreach(DateTimePicker picker in dates)picker.ValueChanged+=delegate{FilterChanged();};pack.ValueChanged+=delegate{FilterChanged();};
            timer.Tick+=async delegate{if(Visible&&!disposed)await FollowTick();};VisibleChanged+=delegate{if(Visible){timer.Start();if(queryPending&&!queryBusy&&!disposed){bool latest=pendingLatest;queryPending=false;Query(latest);}}else{timer.Stop();queryGeneration++;queryPending=false;}};Disposed+=delegate{disposed=true;timer.Stop();timer.Dispose();exportMenu.Dispose();queryGeneration++;queryPending=false;if(exportCancellation!=null)exportCancellation.Cancel();};
            inventoryPanel.Visible=false;inventoryToggle.Text="数据库清单  ▸";RefreshInventory();
        }

        internal string SelectedSource { get { return (string)source.SelectedItem; } }
        internal event EventHandler SelectedSourceChanged;

        static void AddFilter(TableLayoutPanel panel,string caption,Control control,int column){Label label=new Label{Text=caption,Dock=DockStyle.Fill,ForeColor=Color.FromArgb(100,117,138),Font=new Font("Microsoft YaHei UI",8.5f),TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(4,0,0,0)};panel.Controls.Add(label,column,0);panel.Controls.Add(new RoundedInputHost(control){Dock=DockStyle.Fill,Height=36,Margin=new Padding(3,1,3,1)},column,1);}
        void PositionHeaderActions(){int right=headerPanel.ClientSize.Width-8;export.Location=new Point(Math.Max(0,right-export.Width),3);right=export.Left-6;if(cancelExport.Visible){cancelExport.Location=new Point(Math.Max(0,right-cancelExport.Width),5);right=cancelExport.Left-4;}if(exportStatus.Visible){exportStatus.Location=new Point(Math.Max(0,right-exportStatus.Width),10);right=exportStatus.Left-4;}if(exportProgress.Visible)exportProgress.Location=new Point(Math.Max(0,right-exportProgress.Width),9);}
        void SetToNow(){suppressFilter=true;try{DateTime now=DateTime.Now;if(now>to.MaxDate)now=to.MaxDate;if(now<to.MinDate)now=to.MinDate;to.Value=now;}finally{suppressFilter=false;}}
        void FilterChanged(){if(suppressFilter||disposed)return;following=false;beforeId=0;lastFollowTotal=-1;nextPage.Enabled=false;queryGeneration++;Query(true);}
        void SetSource(string value){if(String.IsNullOrEmpty(value))return;int index=source.Items.IndexOf(value);if(index>=0&&source.SelectedIndex!=index){settingSource=true;try{source.SelectedIndex=index;}finally{settingSource=false;}}}
        public void SetActiveSource(string value){activeSource=value;explicitSource=false;beforeId=0;entered=false;queryPending=false;SetSource(value);following=true;if(Visible){SetToNow();queryGeneration++;Query(true);}}
        public void EnterPage(string value){if(!String.IsNullOrEmpty(value))activeSource=value;if(!explicitSource&&activeSource!=null)SetSource(activeSource);if(!entered){entered=true;following=true;beforeId=0;SetToNow();}queryGeneration++;RefreshStatus();RefreshInventory();Query(true);}
        public void RefreshStatus()
        {
            SampleStore s;try{s=getStore((string)source.SelectedItem);}catch(FileNotFoundException){summary.Text=(following?"跟随最新":"历史查询")+"  ·  "+source.SelectedItem+"  ·  尚无数据库";technicalStatus.Text="所选数据源尚无数据库；连接并记录后才会创建。";return;}catch(Exception e){summary.Text="数据库不可用";technicalStatus.Text="数据库无法打开："+e.Message;return;}if(s==null){summary.Text="当前数据库不可用";return;}
            StoreStatus st=s.GetStatus();summary.Text=(following?"● 跟随最新":"○ 历史查询")+"   ·   "+st.Source+"   ·   "+(st.IsRecording?"正在记录":"未记录")+"   ·   "+st.TotalSamples.ToString("N0")+" 条   ·   队列 "+st.PendingCount;
            technicalStatus.Text=st.DatabasePath+(st.AvailableDiskBytes>0?"\r\n所在卷可用空间："+(st.AvailableDiskBytes/1024/1024).ToString("N0")+" MiB"+(st.DiskWarning?"（低于1 GiB）":"")+"；记录队列上限1024项/16 MiB。":"")+"\r\n时间按本机时区筛选、显示与导出，结束时刻不包含。时区："+TimeZoneInfo.Local.Id+(String.IsNullOrEmpty(st.LastError)?"":"\r\n记录错误："+st.LastError)+(String.IsNullOrEmpty(queryError)?"":"\r\n查询错误："+queryError);
        }
        bool inventoryBusy; long inventoryRefreshTicks;
        async void RefreshInventory()
        {
            if(inventoryBusy||disposed)return;inventoryBusy=true;inventoryRefreshTicks=System.Diagnostics.Stopwatch.GetTimestamp();
            try{IList<CatalogEntry> entries=await Task.Run(delegate{return catalog.Inventory();});if(disposed)return;libraries.Rows.Clear();long total=0;foreach(CatalogEntry item in entries){total+=item.Bytes;libraries.Rows.Add(item.Source,item.Period,item.Name,(item.Bytes/1048576.0).ToString("N2"),item.Legacy?"兼容历史库":item.Name.Contains("_cycle_")?"周期分库":"月度分库");}inventorySummary.Text=entries.Count+" 个文件  ·  总占用 "+(total/1048576.0).ToString("N2")+" MiB  ·  旧库只读发现";}catch(Exception e){if(!disposed)inventorySummary.Text="清单暂不可用："+e.Message;}finally{inventoryBusy=false;}
        }
        async Task FollowTick(){if(System.Diagnostics.Stopwatch.GetTimestamp()-inventoryRefreshTicks>=10L*System.Diagnostics.Stopwatch.Frequency)RefreshInventory();RefreshStatus();if(!following)return;SetToNow();string selected=(string)source.SelectedItem;SampleStore store;try{store=getStore(selected);}catch{return;}if(store==null)return;StoreStatus st=store.GetStatus();if(lastFollowSource!=selected||lastFollowTotal!=st.TotalSamples){lastFollowSource=selected;lastFollowTotal=st.TotalSamples;await Query(true);}}
        async Task Query(bool latest)
        {
            if(disposed)return;if(!Visible){queryPending=true;pendingLatest=latest;return;}if(queryBusy){queryPending=true;pendingLatest=latest;return;}queryBusy=true;int request=++queryGeneration;string chosen=(string)source.SelectedItem;long cursor=latest?0:beforeId;DateTime fromUtc=from.Value.ToUniversalTime(),toUtc=to.Value.ToUniversalTime();int selectedPack=(int)pack.Value;
            try{IList<StoredSample> rows=await Task.Run(delegate{return catalog.QueryPage(chosen,fromUtc,toUtc,selectedPack,latest?null:beforeSample,200);});if(disposed||request!=queryGeneration||chosen!=(string)source.SelectedItem)return;grid.Rows.Clear();foreach(StoredSample r in rows)grid.Rows.Add(r.Id,Path.GetFileName(r.Database),r.ReceivedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),r.Address,r.Pack,r.Voltage.ToString("0.00"),r.Current.ToString("+0.00;-0.00;0.00"),r.Soc,r.Soh,r.RemainingAh.ToString("0.00"),r.TotalAh.ToString("0.00"),r.Cycles,String.Join(";",r.Cells),String.Join(";",r.Temperatures));beforeId=rows.Count==0?0:rows[rows.Count-1].Id;beforeSample=rows.Count==0?null:rows[rows.Count-1];nextPage.Enabled=rows.Count==200;queryError=null;RefreshStatus();}
            catch(Exception e){if(!disposed&&request==queryGeneration){queryError=e.Message;if(following)lastFollowTotal=-1;RefreshStatus();}}
            finally{queryBusy=false;if(queryPending&&!disposed&&Visible){bool nextLatest=pendingLatest;queryPending=false;Query(nextLatest);}else if(!Visible||disposed)queryPending=false;}
        }
        async Task Export()
        {
            using(SaveFileDialog d=new SaveFileDialog{InitialDirectory=AppDomain.CurrentDomain.BaseDirectory,Filter="CSV 文件 (*.csv)|*.csv",FileName=ExportNaming.CsvSuggestion((string)source.SelectedItem,(int)pack.Value,from.Value.ToUniversalTime(),to.Value.ToUniversalTime()),AddExtension=true,DefaultExt="csv"})if(d.ShowDialog(this)==DialogResult.OK){exportCancellation=new CancellationTokenSource();SetExporting(true);exportProgress.Visible=true;exportProgress.Style=ProgressBarStyle.Marquee;try{IProgress<XlsxExportProgress> progress=new Progress<XlsxExportProgress>(p=>{exportStatus.Text=p.Phase+" · "+p.CompletedRows.ToString("N0")+" 行";});long n=await catalog.ExportCsvAsync((string)source.SelectedItem,d.FileName,from.Value.ToUniversalTime(),to.Value.ToUniversalTime(),(int)pack.Value,progress,exportCancellation.Token);exportStatus.Text="CSV · "+n.ToString("N0")+" 行完成";}catch(OperationCanceledException){exportStatus.Text="导出已取消";}catch(Exception e){CrashLogger.Write("CSV export failure",e);MessageBox.Show(this,"导出未完成，数据库和既有文件未覆盖："+e.Message,"导出失败",MessageBoxButtons.OK,MessageBoxIcon.Error);}finally{exportCancellation.Dispose();exportCancellation=null;SetExporting(false);}}
        }
        async Task ExportXlsx()
        {
            using(FolderBrowserDialog d=new FolderBrowserDialog{SelectedPath=AppDomain.CurrentDomain.BaseDirectory,Description="选择新建Excel导出目录的父目录",ShowNewFolderButton=true})if(d.ShowDialog(this)==DialogResult.OK){exportCancellation=new CancellationTokenSource();SetExporting(true);exportProgress.Visible=true;exportProgress.Style=ProgressBarStyle.Marquee;exportStatus.Text="固定导出范围…";try{IProgress<XlsxExportProgress> progress=new Progress<XlsxExportProgress>(p=>{exportStatus.Text=p.Phase+" · "+p.CompletedRows.ToString("N0")+(p.TotalRows>0?" / "+p.TotalRows.ToString("N0"):" 行");});XlsxExportResult r=await catalog.ExportXlsxAsync((string)source.SelectedItem,from.Value.ToUniversalTime(),to.Value.ToUniversalTime(),(int)pack.Value,d.SelectedPath,progress,exportCancellation.Token);exportStatus.Text="Excel · "+r.SampleRows.ToString("N0")+" 条完成";MessageBox.Show(this,"已完整导出到：\r\n"+r.OutputDirectory+"\r\n实时数据 "+r.SampleRows+" 条；告警 "+r.AlarmRows+" 条；策略事件 "+r.PolicyRows+" 条。","Excel导出完成",MessageBoxButtons.OK,MessageBoxIcon.Information);}catch(OperationCanceledException){exportStatus.Text="导出已取消";}catch(Exception e){CrashLogger.Write("Excel export failure",e);exportStatus.Text="导出失败";MessageBox.Show(this,"Excel导出未完成；旧结果和数据库未被覆盖。\r\n"+e.Message,"导出失败",MessageBoxButtons.OK,MessageBoxIcon.Error);}finally{exportCancellation.Dispose();exportCancellation=null;SetExporting(false);}}
        }
        void SetExporting(bool value){export.Enabled=!value;query.Enabled=!value;followButton.Enabled=nextPage.Enabled= !value;cancelExport.Visible=value;cancelExport.Enabled=value;exportProgress.Visible=value;exportStatus.Visible=value;PositionHeaderActions();}
    }
}
