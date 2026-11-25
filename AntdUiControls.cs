using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

internal static class AppUiTheme
{
    private static bool initialized;
    internal static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        AntdUI.Config.Mode = AntdUI.TMode.Light;
        AntdUI.Config.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        AntdUI.Config.TextRenderingHighQuality = true;
        AntdUI.Config.ShadowEnabled = false;
        AntdUI.Config.EmojiEnabled = false;
    }
    internal static AntdUI.Input Input(string text)
    {
        Initialize();
        return new AntdUI.Input { Text = text, Font = new Font("Microsoft YaHei UI", 10.5f),
            ForeColor = UiColor.Ink, BackColor = Color.White, Radius = 7, WaveSize = 0,
            BorderWidth = 1, BorderColor = SurfaceColor.Control, BorderHover = SurfaceColor.ControlHover,
            BorderActive = SurfaceColor.Blue, SelectionColor = Color.FromArgb(210, 224, 251) };
    }
}

// Adapt the existing action factory to AntdUI without changing the authentication handlers.
internal sealed class FlatAction : AntdUI.Button
{
    internal bool Refined;
    internal UiSymbol Symbol;
    internal bool StackedSymbol;
    private readonly UiTransition hover, press;
    private bool pointerInside, pointerDown, keyboardDown;
    public int CornerRadius { get { return Radius; } set { Radius = value; } }
    public new Color BackColor
    {
        get { return base.BackColor ?? Color.White; }
        set
        {
            bool primary = value.ToArgb() == UiColor.Blue.ToArgb() || value.ToArgb() == SurfaceColor.Blue.ToArgb();
            Type = primary ? AntdUI.TTypeMini.Primary : AntdUI.TTypeMini.Default;
            base.BackColor = value; DefaultBack = value;
            DefaultBorderColor = primary ? UiColor.Blue : Color.FromArgb(211, 216, 224);
            // AntdUI's bordered Default buttons use these colors for text and border,
            // not the fill. Pale values make hover/pressed text disappear on white.
            BackHover = primary ? Color.FromArgb(33, 77, 147) : UiColor.Blue;
            BackActive = Color.FromArgb(28, 64, 125);
        }
    }
    public new Color ForeColor { get { return base.ForeColor ?? UiColor.Ink; } set { base.ForeColor = value; } }
    public FlatAction()
    {
        AppUiTheme.Initialize();
        Font = new Font("Microsoft YaHei UI", 10.5f);
        Radius = 8; WaveSize = 0; BorderWidth = 1; AutoEllipsis = true;
        AccessibleRole = AccessibleRole.PushButton;
        Cursor = Cursors.Hand;
        BackColor = Color.White; ForeColor = UiColor.Ink;
        hover = new UiTransition(this); press = new UiTransition(this);
        TabStop = true;
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); pointerInside = true; AnimateState(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); pointerInside = false; AnimateState(); }
    protected override void OnMouseDown(MouseEventArgs e)
    { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { pointerDown = true; AnimateState(); } }
    protected override void OnMouseUp(MouseEventArgs e)
    { pointerDown = false; AnimateState(); base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e)
    { base.OnMouseCaptureChanged(e); if (!Capture) { pointerDown = false; AnimateState(); } }
    protected override void OnKeyDown(KeyEventArgs e)
    { if (e.KeyCode == Keys.Space) { keyboardDown = true; AnimateState(); } base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e)
    { if (e.KeyCode == Keys.Space) { keyboardDown = false; AnimateState(); } base.OnKeyUp(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e)
    { keyboardDown = false; AnimateState(); base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e)
    { base.OnEnabledChanged(e); if (!Enabled) { pointerDown = keyboardDown = pointerInside = false; } AnimateState(); }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) { pointerDown = keyboardDown = pointerInside = false; AnimateState(); }
    }
    private void AnimateState()
    {
        if (hover == null || press == null) return;
        hover.To(Enabled && pointerInside ? 1 : 0, 150);
        press.To(Enabled && (keyboardDown || pointerDown && pointerInside) ? 1 : 0, pointerDown || keyboardDown ? 70 : 160);
    }

    // Busy: the action has been accepted and is running. The button keeps its
    // colour (instead of greying out) and shows a small spinner, so a click
    // always gets a visible answer even while Enabled is false.
    private bool busy;
    private Timer spinner;
    internal bool Busy
    {
        get { return busy; }
        set
        {
            if (busy == value) return;
            busy = value;
            if (busy && UiMotion.Enabled)
            {
                if (spinner == null) { spinner = new Timer { Interval = 33 }; spinner.Tick += delegate { if (IsHandleCreated && Visible) Invalidate(); }; }
                spinner.Start();
            }
            else if (spinner != null) spinner.Stop();
            AccessibleDescription = busy ? "正在处理" : null;
            Invalidate();
        }
    }

    private bool IsPrimary { get { return BackColor.ToArgb() == UiColor.Blue.ToArgb() || BackColor.ToArgb() == SurfaceColor.Blue.ToArgb(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Preserve AntdUI activation, accessibility and hit-testing. Only the shared
        // text-action surface is painted here; layout never moves during feedback.
        var g = e.Graphics;
        double h = hover == null ? 0 : hover.Value, p = press == null ? 0 : press.Value;
        bool primary = IsPrimary, live = Enabled || busy;
        if (busy) { h = 0; p = 0; }
        Color behind = UiSurface.Behind(this);
        using (var clear = new SolidBrush(behind)) g.FillRectangle(clear, ClientRectangle);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Face: 1px side margin and 2px below for the contact shadow. Pressing
        // sinks the face by 1px and pulls it in slightly, like a real key.
        int sink = (int)Math.Round(p);
        int squeeze = (int)Math.Round(p * Math.Min(3, Width * .012));
        Rectangle face = new Rectangle(1 + squeeze, 1 + sink, Math.Max(1, Width - 3 - 2 * squeeze), Math.Max(1, Height - 4));

        Color fill, border, text, icon;
        if (!live)
        {
            fill = SurfaceColor.Disabled; border = SurfaceColor.Border; text = icon = SurfaceColor.DisabledText;
        }
        else if (primary)
        {
            fill = UiMotion.Mix(UiMotion.Mix(SurfaceColor.Blue, SurfaceColor.BlueHover, h), SurfaceColor.BluePressed, p);
            border = UiMotion.Mix(fill, SurfaceColor.BluePressed, .35);
            text = icon = busy ? Color.FromArgb(225, 234, 255) : Color.White;
        }
        else
        {
            fill = UiMotion.Mix(UiMotion.Mix(Color.White, Color.FromArgb(246, 248, 252), h), Color.FromArgb(236, 240, 246), p);
            border = UiMotion.Mix(UiMotion.Mix(SurfaceColor.Control, SurfaceColor.ControlHover, h), SurfaceColor.ControlHover, p);
            text = ForeColor.ToArgb() == Color.White.ToArgb() ? SurfaceColor.Ink : ForeColor;
            if (busy) text = SurfaceColor.Muted;
            icon = SurfaceColor.Blue;
        }

        if (live && p < 1)
        {
            // Contact shadow, tinted by the face colour; it grows on hover and
            // collapses when pressed.
            double lift = (1 - p);
            Color tint = primary ? SurfaceColor.Blue : Color.FromArgb(16, 28, 54);
            int a1 = (int)((primary ? 34 + 26 * h : 12 + 8 * h) * lift);
            using (var shadow = CardPanel.MakePath(new Rectangle(face.X, face.Y + 2, face.Width, face.Height), Radius))
            using (var brush = new SolidBrush(Color.FromArgb(Math.Max(0, a1), tint))) g.FillPath(brush, shadow);
            if (primary)
                using (var wide = CardPanel.MakePath(new Rectangle(face.X + 1, face.Y + 3, face.Width - 2, face.Height - 1), Radius))
                using (var brush = new SolidBrush(Color.FromArgb((int)((10 + 18 * h) * lift), tint))) g.FillPath(brush, wide);
        }
        using (var path = CardPanel.MakePath(face, Radius))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(border))
        { g.FillPath(brush, path); g.DrawPath(pen, path); }
        if (live && primary && p < .5)
        {
            // A hairline of light along the top edge gives the face a little
            // thickness without turning it into a glossy gradient.
            using (var hi = new Pen(Color.FromArgb((int)(46 * (1 - p * 2)), Color.White)))
                g.DrawLine(hi, face.X + Radius * .8F, face.Y + 1, face.Right - Radius * .8F, face.Y + 1);
        }

        Rectangle textArea = new Rectangle(9, sink, Math.Max(1, Width - 18), Math.Max(1, Height - 2));
        UiSymbol symbol = Symbol;
        bool spinnerOnly = busy && symbol == UiSymbol.None;
        if (StackedSymbol)
        {
            const int tile = 30, iconSize = 18;
            var plate = new Rectangle((Width - tile) / 2, 7 + sink, tile, tile);
            using (var platePath = CardPanel.MakePath(plate, 9))
            using (var brush = new SolidBrush(Color.FromArgb(live ? (int)(20 + 18 * h + 14 * p) : 10, live ? SurfaceColor.Blue : SurfaceColor.DisabledText)))
                g.FillPath(brush, platePath);
            var glyph = new RectangleF(plate.X + (tile - iconSize) / 2F, plate.Y + (tile - iconSize) / 2F, iconSize, iconSize);
            if (busy) DrawSpinner(g, glyph, icon); else UiSymbols.Draw(g, symbol, glyph, live ? icon : text);
            textArea = new Rectangle(4, plate.Bottom + 2, Math.Max(1, Width - 8), Math.Max(1, Height - plate.Bottom - 6));
        }
        else if (symbol != UiSymbol.None || busy)
        {
            int iconSize = 17;
            int textWidth = Math.Min(Math.Max(0, Width - 54), TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);
            int groupLeft = Math.Max(12, (Width - textWidth - iconSize - 9) / 2);
            var glyph = new RectangleF(groupLeft, (Height - 2 - iconSize) / 2F + sink, iconSize, iconSize);
            if (busy) DrawSpinner(g, glyph, primary ? text : SurfaceColor.Blue);
            else UiSymbols.Draw(g, symbol, glyph, primary || !live ? text : icon);
            textArea = new Rectangle(groupLeft + iconSize + 9, sink, Math.Max(1, Width - groupLeft - iconSize - 18), Math.Max(1, Height - 2));
        }
        TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        if ((symbol != UiSymbol.None || spinnerOnly) && !StackedSymbol) flags = (flags & ~TextFormatFlags.HorizontalCenter) | TextFormatFlags.Left | TextFormatFlags.NoPadding;
        if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(g, Text, Font, textArea, text, flags);

        if (Focused && ShowFocusCues && Enabled)
        {
            // Two-tone ring readable on both the blue and the white face.
            using (var ring = CardPanel.MakePath(Rectangle.Inflate(face, -2, -2), Math.Max(2, Radius - 2)))
            using (var pen = new Pen(primary ? Color.FromArgb(200, Color.White) : Color.FromArgb(150, SurfaceColor.Blue), 1.6F))
                g.DrawPath(pen, ring);
        }
    }

    private static void DrawSpinner(Graphics g, RectangleF bounds, Color color)
    {
        var r = RectangleF.Inflate(bounds, -1.5F, -1.5F);
        using (var track = new Pen(Color.FromArgb(60, color), 2F)) g.DrawEllipse(track, r);
        float start = UiMotion.Enabled ? (Environment.TickCount % 900) / 900F * 360F : 0;
        using (var arc = new Pen(color, 2F) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            g.DrawArc(arc, r, start, 100);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { if (hover != null) hover.Dispose(); if (press != null) press.Dispose(); if (spinner != null) spinner.Dispose(); }
        base.Dispose(disposing);
    }
    protected override AccessibleObject CreateAccessibilityInstance()
    {
        return new ActionAccessibleObject(this);
    }
    private sealed class ActionAccessibleObject : ControlAccessibleObject
    {
        private readonly FlatAction button;
        internal ActionAccessibleObject(FlatAction owner) : base(owner) { button = owner; }
        public override string DefaultAction { get { return "按下"; } }
        public override void DoDefaultAction()
        {
            // UIA Invoke is bridged through the MSAA default action on .NET Framework.
            // ControlAccessibleObject inherits a no-op; use AntdUI's actual button path.
            if (button.IsDisposed || !button.IsHandleCreated) return;
            try
            {
                button.BeginInvoke((MethodInvoker)delegate {
                    if (!button.IsDisposed && button.Visible && button.Enabled)
                        ((IButtonControl)button).PerformClick();
                });
            }
            catch (InvalidOperationException) { }
        }
    }
}

internal sealed class ModernSwitch : AntdUI.Switch
{
    public bool Value { get { return Checked; } set { Checked = value; } }
    public event EventHandler Changed;
    public ModernSwitch()
    {
        AppUiTheme.Initialize();
        Size = new Size(52, 28); Fill = UiColor.Blue; FillHover = SurfaceColor.BlueHover; WaveSize = 0;
        Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.CheckButton;
        CheckedChanged += delegate { if (Changed != null) Changed(this, EventArgs.Empty); };
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
        { Value = !Value; e.Handled = true; e.SuppressKeyPress = true; return; }
        base.OnKeyDown(e);
    }
}

// Small read-only result model. AntdUI.Table is the actual renderer and selection control.
internal sealed class UiResultTable : AntdUI.Table
{
    private readonly DataTable data = new DataTable();
    private int selected = -1;
    private bool rendering;
    public readonly List<UiResultRow> Rows = new List<UiResultRow>();
    public new readonly List<UiResultColumn> Columns = new List<UiResultColumn>();
    public event EventHandler SelectionChanged;
    public UiResultRow CurrentRow { get { return selected >= 0 && selected < Rows.Count ? Rows[selected] : null; } }
    public UiResultCell CurrentCell
    {
        get { return CurrentRow == null ? null : CurrentRow.Cells[0]; }
        set { if (value != null) SelectRow(value.Row.Index); }
    }
    public UiResultTable(params string[] titles)
    {
        AppUiTheme.Initialize();
        Font = new Font("Microsoft YaHei UI", 10.5f); Dock = DockStyle.Fill;
        base.ForeColor = UiColor.Ink; base.BackColor = Color.White;
        ColumnFont = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
        ColumnBack = SurfaceColor.Pale; ColumnFore = UiColor.Muted;
        BorderColor = SurfaceColor.Border; BorderWidth = 1; Bordered = false;
        Radius = 8; RowHeight = 38; RowHeightHeader = 38; Gaps = new Size(12, 9);
        RowHoverBg = Color.FromArgb(247, 249, 252); RowSelectedBg = SurfaceColor.BlueTint;
        RowSelectedFore = UiColor.Ink; LostFocusClearSelection = false; MultipleRows = false;
        EmptyHeader = true; EmptyText = "等待测试"; ShowTip = true;
        AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill;
        base.Columns = new AntdUI.ColumnCollection();
        for (int i = 0; i < titles.Length; i++)
        {
            string key = "value" + i;
            data.Columns.Add(key, typeof(string));
            bool numeric = titles[i].Contains("MB/s") || titles[i].Contains("Mbps") || titles[i].Contains("耗时") || titles[i].Contains("延迟") || titles[i] == "有效采样";
            var native = new AntdUI.Column(key, titles[i], numeric ? AntdUI.ColumnAlign.Right : AntdUI.ColumnAlign.Left)
                { Ellipsis = true, LineBreak = false, ColBreak = false, Editable = false, SortOrder = false };
            base.Columns.Add(native); Columns.Add(new UiResultColumn(this, native));
        }
        DataSource = data;
        CellClick += delegate(object sender, AntdUI.TableClickEventArgs e) { SelectRecord(e.Record); };
        SelectIndexChanged += delegate
        {
            if (rendering) return;
            // AntdUI's visual row zero is the header. This table has no sorting or tree rows.
            int index = base.SelectedIndex - 1;
            if (index >= 0 && index < Rows.Count) SelectRow(index);
        };
        SetRowStyle += delegate(object sender, AntdUI.TableSetRowStyleEventArgs e)
        {
            object value = e.Record;
            var metadata = value as AntdUI.Table.IRow;
            if (metadata != null) value = metadata.record;
            var record = value as DataRow; int index = record == null ? -1 : data.Rows.IndexOf(record);
            if (index < 0 || index >= Rows.Count) return null;
            Color color = Rows[index].DefaultCellStyle.BackColor;
            return color.IsEmpty || color.ToArgb() == Color.White.ToArgb() ? null : new AntdUI.Table.CellStyleInfo { BackColor = color };
        };
        UpdateWidths();
    }
    public void AddRow(params object[] values)
    {
        var record = data.NewRow();
        for (int i = 0; i < data.Columns.Count; i++) record[i] = i < values.Length ? Convert.ToString(values[i], CultureInfo.CurrentCulture) : "";
        data.Rows.Add(record); Rows.Add(new UiResultRow(this, record, Rows.Count));
        if (selected < 0) selected = 0;
        RenderData();
    }
    internal void RenderData()
    {
        if (IsDisposed || rendering) return;
        rendering = true;
        try { base.Refresh(); if (CurrentRow != null) SetSelected(data.Rows[selected]); }
        finally { rendering = false; }
    }
    internal void UpdateWidths()
    {
        if (Columns.Count == 0) return;
        double total = Columns.Sum(c => (double)c.FillWeight);
        // Leave room for the table border and per-column DPI rounding, avoiding a 1px overflow scrollbar.
        for (int i = 0; i < Columns.Count; i++) Columns[i].Native.Width = (Columns[i].FillWeight * 99.0 / total).ToString("0.####", CultureInfo.InvariantCulture) + "%";
        RenderData();
    }
    public void SelectRow(int index)
    {
        if (index < 0 || index >= Rows.Count) return;
        bool changed = selected != index; selected = index;
        if (!rendering) SetSelected(data.Rows[index]);
        if (changed && SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
    }
    private void SelectRecord(object record)
    {
        var metadata = record as AntdUI.Table.IRow;
        if (metadata != null) record = metadata.record;
        var row = record as DataRow; if (row != null) SelectRow(data.Rows.IndexOf(row));
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RenderData(); }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing); if (disposing) data.Dispose();
    }
}
internal sealed class UiResultColumn
{
    private readonly UiResultTable table; internal readonly AntdUI.Column Native;
    private float weight = 100;
    internal UiResultColumn(UiResultTable owner, AntdUI.Column column) { table = owner; Native = column; }
    public float FillWeight { get { return weight; } set { weight = Math.Max(1, value); table.UpdateWidths(); } }
}
internal sealed class UiResultRow
{
    internal readonly UiResultTable Table; internal readonly DataRow Record;
    public readonly int Index; public readonly List<UiResultCell> Cells = new List<UiResultCell>();
    public readonly UiResultStyle DefaultCellStyle;
    internal UiResultRow(UiResultTable table, DataRow record, int index)
    {
        Table = table; Record = record; Index = index; DefaultCellStyle = new UiResultStyle(table);
        for (int i = 0; i < record.Table.Columns.Count; i++) Cells.Add(new UiResultCell(this, i));
    }
    public bool Selected { get { return Table.CurrentRow == this; } set { if (value) Table.SelectRow(Index); } }
    public void SetValues(params object[] values)
    {
        for (int i = 0; i < Cells.Count; i++) Record[i] = i < values.Length ? Convert.ToString(values[i], CultureInfo.CurrentCulture) : "";
        Table.RenderData();
    }
}
internal sealed class UiResultCell
{
    internal readonly UiResultRow Row; private readonly int index;
    internal UiResultCell(UiResultRow row, int column) { Row = row; index = column; }
    public object Value { get { return Row.Record[index]; } set { Row.Record[index] = Convert.ToString(value, CultureInfo.CurrentCulture); Row.Table.RenderData(); } }
    public string ToolTipText { get; set; }
}
internal sealed class UiResultStyle
{
    private readonly UiResultTable table; private Color back = Color.Empty;
    internal UiResultStyle(UiResultTable owner) { table = owner; }
    public Color BackColor { get { return back; } set { back = value; table.RenderData(); } }
}
