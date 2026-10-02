using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BmsSerialDemo
{
    // Visual concept adapted from the user-provided Uiverse.io “contactButton” HTML/CSS by d4niz.
    // Rebuilt with native WinForms drawing; no embedded web view, third-party controls, or animation timer.
    sealed class StyledActionButton : Button
    {
        bool hovered, pressed;
        string iconGlyph = "";
        readonly Font ownedFont;
        public string IconGlyph { get { return iconGlyph; } set { iconGlyph = value ?? ""; Invalidate(); } }
        internal Rectangle TextAreaForTest
        {
            get
            {
                float scale=Math.Max(1f,DeviceDpi/96f);int blockWidth=String.IsNullOrEmpty(iconGlyph)?0:Math.Min((int)(28*scale),Math.Max(0,Width/3));
                return Rectangle.Round(new RectangleF(Padding.Left,scale,Math.Max(0,Width-blockWidth-Padding.Left-Padding.Right),Height-4*scale));
            }
        }
        internal int RequiredTextWidthForTest
        {
            get { return TextRenderer.MeasureText(Text??"",Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine).Width; }
        }
        internal bool TextFitsForTest { get { return TextAreaForTest.Width>=RequiredTextWidthForTest; } }

        public StyledActionButton()
        {
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
            BackColor = Color.White; ForeColor = Color.FromArgb(48, 75, 106);
            ownedFont = new Font("Microsoft YaHei UI", 9, FontStyle.Bold); Font = ownedFont; Height = 36; MinimumSize = new Size(72, 36); AutoSize = false;
            Cursor = Cursors.Hand; TabStop = true; Padding = new Padding(12, 0, 8, 0);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if(disposing)ownedFont.Dispose(); }

        protected override void OnPaintBackground(PaintEventArgs e) { Control parent=Parent; while(parent!=null&&parent.BackColor.A==0)parent=parent.Parent; e.Graphics.Clear(parent==null?SystemColors.Control:parent.BackColor); }
        protected override void OnPaint(PaintEventArgs e)
        {
            OnPaintBackground(e);
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float scale = Math.Max(1f, DeviceDpi / 96f), radius = 8f * scale;
            RectangleF body = new RectangleF(scale, scale, Math.Max(0, Width - 3f*scale), Math.Max(0, Height - 4f*scale));
            if (body.Width < 2 || body.Height < 2) return;
            bool primary=BackColor.R<110&&BackColor.G<170&&BackColor.B<245;
            Color face = !Enabled ? Color.FromArgb(235, 239, 245) : pressed ? (primary?ControlPaint.Dark(BackColor,.08f):Color.FromArgb(241,245,250)) : hovered ? (primary?ControlPaint.Light(BackColor,.06f):Color.FromArgb(247,249,252)) : BackColor;
            Color textColor = Enabled ? (primary?Color.White:ForeColor) : Color.FromArgb(139, 149, 163);
            using (GraphicsPath shape = Rounded(body, radius)) using (SolidBrush brush = new SolidBrush(face)) using(Pen border=new Pen(Color.FromArgb(204,216,230),1f*scale)) { g.FillPath(brush, shape); if(!primary)g.DrawPath(border,shape); }
            int blockWidth = String.IsNullOrEmpty(iconGlyph) ? 0 : Math.Min((int)(28 * scale), Math.Max(0, Width / 3));
            if (blockWidth > 0)
            {
                RectangleF tile = new RectangleF(Width - blockWidth - 2f*scale, 2f*scale, blockWidth, Math.Max(0, Height - 6f*scale));
                using (GraphicsPath tilePath = Rounded(tile, radius * .65f)) using (SolidBrush tileBrush = new SolidBrush(primary?(Enabled ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(18, 80, 90, 110)):Color.FromArgb(239,244,250))) g.FillPath(tileBrush, tilePath);
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                using (SolidBrush glyphBrush = new SolidBrush(textColor)) using (Font glyphFont = new Font("Segoe UI Symbol", 11f, FontStyle.Bold, GraphicsUnit.Point))
                    g.DrawString(iconGlyph, glyphFont, glyphBrush, tile, sf);
            }
            Rectangle textRect = TextAreaForTest;
            TextRenderer.DrawText(g, Text, Font, textRect, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues && Enabled)
            {
                Rectangle focus = Rectangle.Round(new RectangleF(5 * scale, 4 * scale, Width - 10 * scale, Height - 11 * scale));
                ControlPaint.DrawFocusRectangle(g, focus, textColor, Color.Transparent);
            }
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }

        [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
        internal static int VerifyRepeatedDrawing()
        {
            using(System.Diagnostics.Process process=System.Diagnostics.Process.GetCurrentProcess())
            {
                uint before=GetGuiResources(process.Handle,0),beforeUser=GetGuiResources(process.Handle,1);
                using(Form host=new Form{Size=new Size(260,80),ShowInTaskbar=false})
                using(StyledActionButton button=new StyledActionButton{Text="查询记录",IconGlyph="⌕",Size=new Size(180,38),Location=new Point(8,8),BackColor=Color.FromArgb(65,111,232)})
                using(TextBox editor=new TextBox{Text="ime"})
                using(RoundedInputHost input=new RoundedInputHost(editor){Size=new Size(190,36),Location=new Point(8,48)})
                using(StyledCheckBox check=new StyledCheckBox{Text="全部 Pack",Location=new Point(202,12),Checked=true})
                using(Bitmap bitmap=new Bitmap(180,38))
                {
                    host.Controls.Add(button);host.Controls.Add(input);host.Controls.Add(check);IntPtr ignored=button.Handle;
                    using(Bitmap editorBitmap=new Bitmap(190,36))using(Bitmap checkBitmap=new Bitmap(92,32))
                    for(int i=0;i<600;i++){button.Enabled=(i%4)!=0;button.IconGlyph=(i%2)==0?"↓":"▶";button.Text=(i%2)==0?"查询记录":"导出数据";button.DrawToBitmap(bitmap,new Rectangle(Point.Empty,button.Size));input.DrawToBitmap(editorBitmap,new Rectangle(Point.Empty,input.Size));check.Checked=(i%2)==0;check.DrawToBitmap(checkBitmap,new Rectangle(Point.Empty,check.Size));}
                }
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();uint after=GetGuiResources(process.Handle,0),afterUser=GetGuiResources(process.Handle,1);
                if(after>before+6||afterUser>beforeUser+4)throw new InvalidOperationException("Native UI repeated paint leaked GDI/USER handles (GDI="+before+"→"+after+", USER="+beforeUser+"→"+afterUser+")");
                return 1;
            }
        }
    }
}
