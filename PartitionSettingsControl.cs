using System;
using System.Drawing;
using System.Windows.Forms;

namespace BmsSerialDemo
{
    internal sealed class PartitionSettingsRequestedEventArgs : EventArgs
    {
        public int Days { get; private set; }
        public bool ApplyImmediately { get; private set; }
        public PartitionSettingsRequestedEventArgs(int days, bool immediate) { Days = days; ApplyImmediately = immediate; }
    }

    // Thin UI only: the caller owns persistence and applies changes at a completed acquisition-round boundary.
    internal sealed class PartitionSettingsControl : UserControl
    {
        readonly Func<string> getSummary;
        readonly ComboBox preset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 96 };
        readonly NumericUpDown custom = new NumericUpDown { Minimum = 1, Maximum = 3650, Value = 30, Width = 76, Enabled = false };
        readonly Label summary = new Label { AutoSize = true, ForeColor = Color.FromArgb(55, 75, 98), Margin = new Padding(10, 7, 10, 0) };
        readonly StyledActionButton next = new StyledActionButton { Text = "下个分期生效", Width=128, Height=36 };
        readonly StyledActionButton immediate = new StyledActionButton { Text = "立即新分期", Width=128, Height=36 };
        readonly LinkLabel toggle = new LinkLabel { Text = "分库周期与状态  ▸", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, LinkColor = Color.FromArgb(45,108,175), Padding = new Padding(8,0,0,0) };
        readonly FlowLayoutPanel row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, BackColor = Color.White, Padding = new Padding(2), Visible = false };
        public event EventHandler<PartitionSettingsRequestedEventArgs> Requested;
        internal string SummaryText { get { return summary.Text; } }

        public PartitionSettingsControl(Func<string> summaryProvider)
        {
            getSummary = summaryProvider ?? delegate { return "分期状态暂不可用"; };
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; BackColor = Color.White; Padding = new Padding(4,2,4,2); Margin = new Padding(0,0,0,4);
            preset.Items.AddRange(new object[] { "7 天", "15 天", "30 天", "自定义" }); preset.SelectedIndex = 2;
            row.Controls.Add(new Label { Text = "分库周期", AutoSize = true, Margin = new Padding(4, 7, 2, 0) }); row.Controls.Add(preset); row.Controls.Add(custom); row.Controls.Add(next); row.Controls.Add(immediate); row.Controls.Add(summary);
            TableLayoutPanel shell = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, BackColor = Color.White, Padding = Padding.Empty, Margin = Padding.Empty };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute,28)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shell.Controls.Add(toggle,0,0); shell.Controls.Add(row,0,1); Controls.Add(shell);
            toggle.LinkClicked += delegate { row.Visible=!row.Visible; toggle.Text=row.Visible?"分库周期与状态  ▾":"分库周期与状态  ▸"; PerformLayout(); };
            preset.SelectedIndexChanged += delegate { custom.Enabled = preset.SelectedIndex == 3; };
            next.Click += delegate { Raise(false); }; immediate.Click += delegate { Raise(true); };
            RefreshSummary();
        }

        public void RefreshSummary()
        {
            try { summary.Text = getSummary(); }
            catch (Exception ex) { summary.Text = "分期状态暂不可用：" + ex.Message; }
        }

        int SelectedDays { get { return preset.SelectedIndex == 0 ? 7 : preset.SelectedIndex == 1 ? 15 : preset.SelectedIndex == 2 ? 30 : (int)custom.Value; } }
        void Raise(bool applyImmediately)
        {
            EventHandler<PartitionSettingsRequestedEventArgs> handler = Requested;
            if (handler != null) handler(this, new PartitionSettingsRequestedEventArgs(SelectedDays, applyImmediately));
        }
    }
}
