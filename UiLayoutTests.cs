using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace BmsSerialDemo
{
    internal static class UiLayoutTests
    {
        static int checks;
        static void Check(bool value, string label) { if (!value) throw new Exception("UI布局测试失败：" + label); checks++; }

        // 125%/150% cases scale the WinForms control tree in isolation; they do not change Windows display scaling.
        internal static int Run()
        {
            checks = 0;
            Program.ConfigureApplicationStylesAndDpi();
            Check(Application.RenderWithVisualStyles, "正式启动和截图共用VisualStyles初始化");
            VerifyLayout(1.0f, 410, "100%");
            VerifyLayout(1.25f, 512, "125%模拟");
            VerifyLayout(1.5f, 615, "150%模拟");
            VerifyLayout(1.0f, 1180, "宽窗口");
            VerifyActionText(1.0f, "100%"); VerifyActionText(1.25f, "125%模拟"); VerifyActionText(1.5f, "150%模拟");
            VerifyCheckBoxRendering();
            VerifyMainFormLayouts();
            return checks;
        }

        static void VerifyMainFormLayouts()
        {
            string testRoot=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"bms-ui-layout-"+Guid.NewGuid().ToString("N"));
            using(MainForm form=new MainForm(testRoot,System.IO.Path.Combine(testRoot,"settings","recording-period.txt")))
            {
                form.ShowInTaskbar=false;form.Opacity=0;form.WindowState=FormWindowState.Normal;form.Show();
                TabControl tabs=(TabControl)typeof(MainForm).GetField("mainTabs",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                Check(tabs!=null,"真实主窗口导航存在");
                VerifyMainFormSize(form,tabs,2560,1600,"2560×1600");
                VerifyMainFormSize(form,tabs,1360,920,"1360×920");
                VerifyMainFormSize(form,tabs,1050,760,"1050×760");
            }
        }

        static void VerifyMainFormSize(MainForm form,TabControl tabs,int width,int height,string label)
        {
            form.ClientSize=new Size(width,height);form.PerformLayout();Application.DoEvents();
            TabPage live=null,record=null;foreach(TabPage tab in tabs.TabPages){if(tab.Text=="实时总览")live=tab;if(tab.Text=="数据记录")record=tab;}
            Check(live!=null&&record!=null,label+"实时和记录页面存在");
            tabs.SelectedTab=live;PerformTreeLayout(live);Application.DoEvents();
            TableLayoutPanel liveLayout=FindControl<TableLayoutPanel>(live,c=>c.RowCount==4);
            Panel viewport=liveLayout==null?null:liveLayout.Parent as Panel;
            TrendControl trend=FindControl<TrendControl>(live,c=>true);
            Check(liveLayout!=null&&viewport!=null,label+"实时页面使用滚动布局");
            Check(liveLayout.Height>=viewport.ClientSize.Height-8,label+"实时内容高度填满视口或允许纵向滚动");
            Check(trend!=null&&trend.Height>=160,label+"趋势图保留可见绘图区");
            if(width<=1050)
            {
                Label connectionState=(Label)typeof(MainForm).GetField("connectionState",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                Control head=connectionState==null?null:connectionState.Parent;
                Check(connectionState!=null&&head!=null&&head.ClientRectangle.Contains(connectionState.Bounds),label+"窄屏连接状态完整显示在工具栏内");
            }
            if(width<=1050)Check(viewport.VerticalScroll.Maximum>viewport.ClientSize.Height-1,label+"窄屏实时页面可纵向滚动到趋势");

            tabs.SelectedTab=record;PerformTreeLayout(record);Application.DoEvents();
            DataGridView grid=FindControl<DataGridView>(record,c=>c.ReadOnly);
            Check(grid!=null&&grid.ClientSize.Width>100,label+"记录表格已完成布局");
            if(width>=2000)
            {
                int visibleColumns=grid.Columns.GetColumnsWidth(DataGridViewElementStates.Visible);
                Check(visibleColumns>=grid.ClientSize.Width-8,label+"宽屏记录表格列铺满可用宽度");
            }
            else
            {
                Check(grid.Columns.Count>2&&grid.Columns[2].MinimumWidth>=230,label+"窄屏时间列保留完整本地毫秒宽度");
                Check(grid.ScrollBars==ScrollBars.Both,label+"窄屏记录表格可横向滚动");
            }
        }

        static T FindControl<T>(Control root,Func<T,bool> match) where T:Control
        {
            foreach(Control child in root.Controls)
            {
                T candidate=child as T;if(candidate!=null&&(match==null||match(candidate)))return candidate;
                T nested=FindControl<T>(child,match);if(nested!=null)return nested;
            }
            return null;
        }

        static void PerformTreeLayout(Control root)
        {
            root.PerformLayout();foreach(Control child in root.Controls)PerformTreeLayout(child);
        }

        static void VerifyCheckBoxRendering()
        {
            using(Form form=new Form{Size=new Size(260,90),ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.None})
            using(StyledCheckBox check=new StyledCheckBox{Text="模拟设备",Location=new Point(8,8),Checked=true})
            {
                form.Controls.Add(check);form.PerformLayout();IntPtr handle=check.Handle;
                Check(check.HasSinglePaintPath&&!((object)check is CheckBox),"复选框句柄由单一自绘Control承载，没有原生Button绘制");
                Check(check.GetPreferredSize(Size.Empty).Width>=TextRenderer.MeasureText(check.Text,check.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine).Width,"自绘复选框首选宽度容纳完整文字");
                using(Bitmap enabledChecked=RenderCheckBox(check))
                {
                    Check(HasVisiblePixels(enabledChecked),"真实DrawToBitmap能看到复选框和文字");
                    check.Enabled=false;using(Bitmap disabledChecked=RenderCheckBox(check))
                    {Check(BitmapsDiffer(enabledChecked,disabledChecked),"真实DrawToBitmap绘制随禁用状态变化");}
                    check.Enabled=true;using(Bitmap enabledCheckedAgain=RenderCheckBox(check))
                    {
                        check.Checked=false;using(Bitmap enabledUnchecked=RenderCheckBox(check))
                        {Check(BitmapsDiffer(enabledCheckedAgain,enabledUnchecked),"真实DrawToBitmap绘制随Checked状态变化");}
                    }
                }
            }
        }

        static Bitmap RenderCheckBox(StyledCheckBox check)
        {
            Bitmap image=new Bitmap(Math.Max(1,check.Width),Math.Max(1,check.Height));
            using(Graphics g=Graphics.FromImage(image))g.Clear(Color.White);
            check.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));return image;
        }

        static bool BitmapsDiffer(Bitmap first,Bitmap second)
        {
            if(first.Size!=second.Size)return true;
            for(int y=0;y<first.Height;y++)for(int x=0;x<first.Width;x++)if(first.GetPixel(x,y)!=second.GetPixel(x,y))return true;
            return false;
        }

        static bool HasVisiblePixels(Bitmap image)
        {
            for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++)if(image.GetPixel(x,y)!=Color.White)return true;
            return false;
        }

        static void VerifyActionText(float scale, string label)
        {
            using(Form form=new Form{AutoScaleMode=AutoScaleMode.Dpi,AutoScaleDimensions=new SizeF(96,96),ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.None})
            using(FlowLayoutPanel row=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true,AutoScroll=true})
            using(StyledActionButton poll=new StyledActionButton{Text="开始轮询",IconGlyph="▶",Width=144,Height=34})
            using(StyledActionButton send=new StyledActionButton{Text="发送读取",IconGlyph="⌕",Width=144,Height=34})
            {
                form.ClientSize=new Size(420,100);form.Controls.Add(row);row.Controls.Add(poll);row.Controls.Add(send);
                if(scale!=1f)form.Scale(new SizeF(scale,scale));form.PerformLayout();row.PerformLayout();IntPtr pollHandle=poll.Handle;IntPtr sendHandle=send.Handle;
                Check(poll.TextFitsForTest&&send.TextFitsForTest,label+"主功能按钮全文有足够绘制区");
                poll.Text="停止轮询";Check(poll.TextFitsForTest,label+"轮询切换后全文有足够绘制区");
                using(Bitmap image=new Bitmap(poll.Width+send.Width+8,Math.Max(poll.Height,send.Height)))
                {poll.DrawToBitmap(image,new Rectangle(0,0,poll.Width,poll.Height));send.DrawToBitmap(image,new Rectangle(poll.Width+8,0,send.Width,send.Height));}
            }
        }

        static void VerifyLayout(float scale, int clientWidth, string label)
        {
            using (Form form = new Form { AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96, 96), ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None })
            using (FlowLayoutPanel row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, Padding = new Padding(6), BackColor = Color.White })
            using (StyledCheckBox check = new StyledCheckBox { Text = "记录原始收发日志", Checked = true })
            using (TextBox text = new TextBox { Width = 110 })
            using (NumericUpDown number = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 60000, Value = 1500 })
            using (ComboBox combo = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList })
            {
                form.ClientSize = new Size(1180, 180); form.Controls.Add(row);
                check.Size = check.GetPreferredSize(Size.Empty); row.Controls.Add(check);
                MainForm.Add(row, "单次读取", text); MainForm.Add(row, "超时 ms", number); MainForm.Add(row, "命令", combo);
                if (scale != 1f) form.Scale(new SizeF(scale, scale));
                form.ClientSize = new Size(clientWidth, (int)Math.Ceiling(180 * scale));
                form.PerformLayout(); row.PerformLayout();
                IntPtr forcedCheckboxHandle=check.Handle;

                Check(check.HasSinglePaintPath, label + "创建窗口句柄后复选框仍使用单一自绘Control");
                Check(check.Width >= check.GetPreferredSize(Size.Empty).Width, label + "自绘复选框首选宽度容纳文字");
                Check(row.Controls.Count == 4, label + "标签和编辑器按组加入FlowLayout");
                for (int i = 1; i < row.Controls.Count; i++)
                {
                    TableLayoutPanel group = row.Controls[i] as TableLayoutPanel;
                    Check(group != null && group.Controls.Count == 2, label + "每组保留标签与编辑器");
                    Label caption = group.Controls[0] as Label; RoundedInputHost host = group.Controls[1] as RoundedInputHost;
                    Check(caption != null && host != null && group.ClientRectangle.Contains(caption.Bounds) && group.ClientRectangle.Contains(host.Bounds), label + "标签和输入框都在组合内");
                    Check(host.ClientRectangle.Contains(host.Editor.Bounds), label + "数值/文本编辑器位于圆角输入框内");
                }
                for (int i = 0; i < row.Controls.Count; i++) for (int j = i + 1; j < row.Controls.Count; j++)
                    Check(!row.Controls[i].Bounds.IntersectsWith(row.Controls[j].Bounds), label + "FlowLayout组之间没有重叠");

                using (Bitmap bitmap = new Bitmap(Math.Max(1, row.Width), Math.Max(1, row.Height))) row.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            }
        }
    }
}
