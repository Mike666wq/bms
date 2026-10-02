using System.Drawing;
using System.Windows.Forms;

namespace BmsSerialDemo
{
    internal static class UiTheme
    {
        internal static readonly Color Canvas = Color.FromArgb(243, 247, 251);
        internal static readonly Color Surface = Color.White;
        internal static readonly Color Ink = Color.FromArgb(31, 52, 75);
        internal static readonly Color Muted = Color.FromArgb(100, 119, 141);
        internal static readonly Color Blue = Color.FromArgb(54, 112, 220);
        internal static readonly Color Line = Color.FromArgb(218, 227, 237);

        internal static void Apply(Control root)
        {
            if (root == null) return;
            foreach (Control c in root.Controls)
            {
                if (c is TabControl) { c.BackColor = Canvas; }
                else if (c is GroupBox) { c.BackColor = Surface; c.ForeColor = Ink; }
                else if (c is DataGridView)
                {
                    DataGridView g = (DataGridView)c;
                    g.BackgroundColor = Surface; g.GridColor = Line; g.BorderStyle = BorderStyle.None;
                    g.EnableHeadersVisualStyles = false;
                    g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 241, 248);
                    g.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
                    g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(235, 241, 248);
                    g.DefaultCellStyle.BackColor = Surface; g.DefaultCellStyle.ForeColor = Ink;
                    g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 235, 252);
                    g.DefaultCellStyle.SelectionForeColor = Ink; g.RowTemplate.Height = 28;
                }
                else if (c is TextBox) { c.BackColor = Surface; c.ForeColor = Ink; }
                else if (c is ComboBox || c is NumericUpDown || c is DateTimePicker) { c.ForeColor = Ink; }
                else if (c is Button && !(c is StyledActionButton))
                {
                    Button b = (Button)c;
                    b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Line;
                    b.BackColor = Surface; b.ForeColor = Ink; b.Cursor = Cursors.Hand;
                    if (b.Height < 36) b.Height = 36;
                }
                Apply(c);
            }
        }
    }
}
