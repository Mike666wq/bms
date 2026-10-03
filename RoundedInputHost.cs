using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BmsSerialDemo
{
    // Visual treatment adapted from the user-provided Uiverse.io “searchbar” by TimTrayler.
    // Editors remain native WinForms controls so IME, selection, password masking, and dropdown behavior are preserved.
    sealed class RoundedInputHost : Panel
    {
        static readonly Font editorFont = new Font("Microsoft YaHei UI",9,FontStyle.Regular);
        readonly Control editor;
        readonly ToolTip tooltip = new ToolTip();
        bool focused, hovered;
        string errorText;
        public Control Editor { get { return editor; } }
        public string ErrorText
        {
            get { return errorText; }
            set { errorText=value;tooltip.SetToolTip(editor,String.IsNullOrEmpty(value)?null:value);Invalidate(); }
        }
        public RoundedInputHost(Control control)
        {
            if(control==null)throw new ArgumentNullException("control");editor=control;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
            BackColor=Color.White;Height=36;MinimumSize=new Size(42,36);Margin=new Padding(4,3,5,3);Padding=new Padding(9,2,9,2);
            BorderStyle=BorderStyle.None;
            if(control is TextBox)((TextBox)control).BorderStyle=BorderStyle.None;
            if(control is ComboBox)((ComboBox)control).FlatStyle=FlatStyle.Flat;
            if(control is NumericUpDown){NumericUpDown numeric=(NumericUpDown)control;numeric.BorderStyle=BorderStyle.None;numeric.TextAlign=HorizontalAlignment.Center;}
            // U1：圆角宿主内的 ComboBox/DateTimePicker/NumericUpDown 关闭系统主题，3D 立体边框/按钮
            // 退化为细平面外观，与宿主圆角边框不再叠出“双边框”； IME/下拉/键盘语义保持原生。
            if(control is ComboBox||control is DateTimePicker||control is NumericUpDown)control.HandleCreated+=delegate{try{SetWindowTheme(control.Handle,"","");}catch{}};
            control.Font=editorFont;
            control.Dock=control is NumericUpDown?DockStyle.None:DockStyle.Fill;control.Margin=Padding.Empty;control.BackColor=Color.White;Controls.Add(control);
            control.Enter+=delegate{focused=true;Invalidate();};control.Leave+=delegate{focused=false;Invalidate();};
            MouseEnter+=delegate{hovered=true;Invalidate();};MouseLeave+=delegate{hovered=false;Invalidate();};
            control.MouseEnter+=delegate{hovered=true;Invalidate();};control.MouseLeave+=delegate{hovered=false;Invalidate();};
            control.EnabledChanged+=delegate{control.BackColor=control.Enabled?Color.White:Color.FromArgb(240,243,247);Invalidate();};
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            NumericUpDown numeric=editor as NumericUpDown;
            if(numeric==null)return;
            // Native number editors keep their font-dependent height. Center that
            // complete editor (including its arrows) inside the rounded field.
            int height=Math.Min(numeric.PreferredHeight,Math.Max(1,ClientSize.Height-Padding.Vertical));
            numeric.Bounds=new Rectangle(Padding.Left,Math.Max(Padding.Top,(ClientSize.Height-height)/2),Math.Max(1,ClientSize.Width-Padding.Horizontal),height);
        }
        protected override void OnEnabledChanged(EventArgs e){if(editor!=null){editor.Enabled=Enabled;if(!Enabled)focused=false;}Invalidate();base.OnEnabledChanged(e);}
        protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing)tooltip.Dispose();}
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);Graphics g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            float scale=Math.Max(1f,DeviceDpi/96f);RectangleF rect=new RectangleF(scale,scale,Math.Max(0,Width-3*scale),Math.Max(0,Height-3*scale));if(rect.Width<4||rect.Height<4)return;
            bool usable=Enabled&&editor.Enabled;bool readOnly=editor is TextBox&&((TextBox)editor).ReadOnly;Color bg=!usable?Color.FromArgb(240,243,247):readOnly?Color.FromArgb(246,248,251):Color.White;
            using(GraphicsPath path=Rounded(rect,8f*scale))
            {
                using(SolidBrush fill=new SolidBrush(bg))g.FillPath(fill,path);
                Color border=!usable||readOnly?Color.FromArgb(211,218,228):!String.IsNullOrEmpty(errorText)?Color.FromArgb(206,75,75):focused?Color.FromArgb(70,116,232):hovered?Color.FromArgb(178,193,211):Color.FromArgb(211,219,229);
                using(Pen pen=new Pen(border,(focused||!String.IsNullOrEmpty(errorText)?1.5f:1f)*scale))g.DrawPath(pen,path);
            }
        }
        static GraphicsPath Rounded(RectangleF r,float radius)
        {
            GraphicsPath p=new GraphicsPath();float d=Math.Min(radius*2,Math.Min(r.Width,r.Height));p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
        }
        [DllImport("uxtheme.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]static extern int SetWindowTheme(IntPtr hwnd,string pszSubAppName,string pszSubIdList);
    }

    class StyledCheckBox : Control
    {
        bool isChecked, spacePressed;
        public event EventHandler CheckedChanged;
        public bool Checked
        {
            get { return isChecked; }
            set { if(isChecked==value)return;isChecked=value;Invalidate();EventHandler handler=CheckedChanged;if(handler!=null)handler(this,EventArgs.Empty); }
        }
        public StyledCheckBox(){AutoSize=true;Padding=new Padding(2,2,4,2);BackColor=Color.White;ForeColor=Color.FromArgb(45,65,88);TabStop=true;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);AccessibleRole=AccessibleRole.CheckButton;}
        public override Size GetPreferredSize(Size proposedSize){float s=Math.Max(1f,DeviceDpi/96f);Size text=TextRenderer.MeasureText(Text??"",Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine);return new Size(Padding.Left+Padding.Right+(int)Math.Ceiling(22*s)+text.Width+(int)Math.Ceiling(4*s),(int)Math.Max(28*s,text.Height+Padding.Vertical+4*s));}
        protected override void OnMouseEnter(EventArgs e){Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){Invalidate();base.OnMouseLeave(e);}
        protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
        protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
        protected override void OnLostFocus(EventArgs e){spacePressed=false;Invalidate();base.OnLostFocus(e);}
        protected override void OnClick(EventArgs e){if(Enabled)Checked=!Checked;base.OnClick(e);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space&&Enabled){spacePressed=true;e.Handled=true;e.SuppressKeyPress=true;return;}base.OnKeyDown(e);}
        protected override void OnKeyUp(KeyEventArgs e){if(e.KeyCode==Keys.Space&&spacePressed){spacePressed=false;e.Handled=true;if(Enabled)OnClick(EventArgs.Empty);return;}base.OnKeyUp(e);}
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float s=Math.Max(1f,DeviceDpi/96f);float side=15*s,x=Padding.Left,y=(Height-side)/2f;
            RectangleF box=new RectangleF(x,y,side,side);bool hovered=ClientRectangle.Contains(PointToClient(MousePosition));Color border=!Enabled?Color.FromArgb(194,203,215):Checked?Color.FromArgb(65,111,232):hovered?Color.FromArgb(107,137,182):Color.FromArgb(155,171,190);
            using(GraphicsPath shape=Rounded(box,3*s))using(SolidBrush fill=new SolidBrush(!Enabled?Color.FromArgb(240,243,247):Checked?Color.FromArgb(65,111,232):hovered?Color.FromArgb(246,249,253):Color.White))using(Pen pen=new Pen(border,1.2f*s)){g.FillPath(fill,shape);g.DrawPath(pen,shape);}
            if(Checked){using(Pen check=new Pen(Enabled?Color.White:Color.FromArgb(141,151,166),1.8f*s)){check.StartCap=LineCap.Round;check.EndCap=LineCap.Round;g.DrawLines(check,new[]{new PointF(x+3.4f*s,y+7.8f*s),new PointF(x+6.2f*s,y+10.5f*s),new PointF(x+11.8f*s,y+4.2f*s)});}}
            Rectangle textRect=Rectangle.Round(new RectangleF(x+side+7*s,0,Math.Max(0,Width-(x+side+7*s)-Padding.Right),Height));Color text=Enabled?ForeColor:Color.FromArgb(143,152,164);TextRenderer.DrawText(g,Text,Font,textRect,text,TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
            if(Focused&&ShowFocusCues&&Enabled)ControlPaint.DrawFocusRectangle(g,Rectangle.Round(new RectangleF(0,1,Width-1,Height-2)),text,Color.Transparent);
        }
        internal bool HasUserPaintStyle { get { return GetStyle(ControlStyles.UserPaint); } }
        internal bool HasSinglePaintPath { get { return typeof(StyledCheckBox).BaseType==typeof(Control)&&HasUserPaintStyle; } }
        internal Rectangle CustomTextBounds { get { float s=Math.Max(1f,DeviceDpi/96f),side=15*s,x=Padding.Left;return Rectangle.Round(new RectangleF(x+side+7*s,0,Math.Max(0,Width-(x+side+7*s)-Padding.Right),Height)); } }
        static GraphicsPath Rounded(RectangleF r,float radius){GraphicsPath p=new GraphicsPath();float d=Math.Min(radius*2,Math.Min(r.Width,r.Height));p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
        sealed class ClickProbe:StyledCheckBox{internal void ClickForTest(){base.OnClick(EventArgs.Empty);}internal void SpaceForTest(){base.OnKeyDown(new KeyEventArgs(Keys.Space));base.OnKeyUp(new KeyEventArgs(Keys.Space));}}
        internal static bool VerifyToggleSemantics(){using(Form host=new Form{Size=new Size(220,70),ShowInTaskbar=false})using(ClickProbe check=new ClickProbe{Text="模拟设备",Location=new Point(8,8),Checked=false}){host.Controls.Add(check);IntPtr handle=check.Handle;int changes=0;check.CheckedChanged+=delegate{changes++;};check.ClickForTest();bool first=check.Checked;check.ClickForTest();bool clickToggle=first&&!check.Checked;check.SpaceForTest();return clickToggle&&check.Checked&&changes==3;}}
    }
}
