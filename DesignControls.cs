using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// One palette for every page. Neutral cool greys carry the structure; the
// school blue is reserved for actions, selection and live download data.
internal static class SurfaceColor
{
    internal static readonly Color Blue = Color.FromArgb(31, 91, 216);        // #1F5BD8 primary action
    internal static readonly Color BlueHover = Color.FromArgb(26, 79, 192);   // #1A4FC0
    internal static readonly Color BluePressed = Color.FromArgb(22, 63, 156); // #163F9C
    internal static readonly Color BlueTint = Color.FromArgb(235, 241, 254);  // #EBF1FE selection wash
    internal static readonly Color Green = Color.FromArgb(15, 123, 85);       // #0F7B55
    internal static readonly Color GreenTint = Color.FromArgb(231, 245, 238);
    internal static readonly Color Amber = Color.FromArgb(169, 98, 15);       // #A9620F
    internal static readonly Color AmberTint = Color.FromArgb(255, 244, 229);
    internal static readonly Color Red = Color.FromArgb(180, 35, 24);
    internal static readonly Color Ink = Color.FromArgb(20, 32, 57);          // #142039
    internal static readonly Color Muted = Color.FromArgb(91, 103, 125);      // #5B677D
    internal static readonly Color Faint = Color.FromArgb(139, 149, 167);     // #8B95A7 axis text
    internal static readonly Color Canvas = Color.FromArgb(243, 245, 249);    // #F3F5F9
    internal static readonly Color Pale = Color.FromArgb(246, 248, 251);      // #F6F8FB inner wells
    internal static readonly Color Border = Color.FromArgb(228, 232, 239);    // #E4E8EF hairline
    internal static readonly Color Control = Color.FromArgb(217, 223, 232);   // #D9DFE8 field / button edge
    internal static readonly Color ControlHover = Color.FromArgb(190, 199, 214);
    internal static readonly Color Disabled = Color.FromArgb(242, 244, 247);
    internal static readonly Color DisabledText = Color.FromArgb(154, 163, 178);
}

internal static class UiFont
{
    internal const string Family = "Microsoft YaHei UI";
    private static string numeric;
    // Live figures use a squarer, engineered face when Windows has one, so
    // changing digits read like an instrument rather than body text.
    internal static string NumericFamily
    {
        get
        {
            if (numeric != null) return numeric;
            foreach (string candidate in new[] { "Bahnschrift SemiBold", "Segoe UI Semibold" })
            {
                try { using (var probe = new Font(candidate, 10f)) if (String.Equals(probe.Name, candidate, StringComparison.OrdinalIgnoreCase)) return numeric = candidate; }
                catch (ArgumentException) { }
            }
            return numeric = Family;
        }
    }
    internal static Font Numeric(float size)
    {
        string family = NumericFamily;
        return new Font(family, size, family == Family ? FontStyle.Bold : FontStyle.Regular);
    }
    internal static bool IsAscii(string text)
    {
        foreach (char c in text ?? "") if (c > 126 && c != '\u2014') return false;
        return true;
    }
}

internal static class UiSurface
{
    // The opaque colour actually visible behind a control. Cards are drawn
    // transparent, so their Fill (not their BackColor) is what sits behind.
    internal static Color Behind(Control control)
    {
        for (Control parent = control.Parent; parent != null; parent = parent.Parent)
        {
            var card = parent as CardPanel; if (card != null) return card.Fill;
            var surface = parent as SurfacePanel; if (surface != null) return surface.Fill;
            if (parent.BackColor.A == 255) return parent.BackColor;
        }
        return Color.White;
    }

    // Soft two-layer elevation, painted by the page beneath each card so the
    // blur can extend past the card's own bounds.
    internal static void AttachCardShadows(Control host)
    {
        host.Paint += delegate(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; var state = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (Control child in host.Controls)
                {
                    var card = child as CardPanel;
                    if (card == null || !card.Visible || !card.Elevated || card.Width < 8 || card.Height < 8) continue;
                    Rectangle r = card.Bounds;
                    if (!Rectangle.Inflate(r, 12, 14).IntersectsWith(e.ClipRectangle)) continue;
                    DrawElevation(g, r, card.Radius);
                }
            }
            finally { g.Restore(state); }
        };
    }
    internal static void DrawElevation(Graphics g, Rectangle r, int radius)
    {
        // Ambient: wide, very faint. Key: tight, slightly darker, offset down.
        int[] spread = { 8, 5, 2 }; int[] alpha = { 4, 6, 9 };
        for (int i = 0; i < spread.Length; i++)
        {
            var layer = new Rectangle(r.X - spread[i] / 2, r.Y + spread[i] / 2, r.Width + spread[i] - 1, r.Height + spread[i] / 2 - 1);
            using (var path = CardPanel.MakePath(layer, radius + spread[i] / 2))
            using (var brush = new SolidBrush(Color.FromArgb(alpha[i], 16, 28, 54))) g.FillPath(brush, path);
        }
        using (var key = CardPanel.MakePath(new Rectangle(r.X, r.Y + 1, r.Width - 1, r.Height - 1), radius))
        using (var brush = new SolidBrush(Color.FromArgb(14, 16, 28, 54))) g.FillPath(brush, key);
    }
}

internal enum UiSymbol { None, Home, Pulse, User, Cloud, Tools, Settings, Info, Check, Down, Up, Clock, Laptop, Chart, File, Wrench, Play, Link, Globe, Speed, Copy, Stop, Wifi, List, Bulb, Refresh, External, Router, Shield, Game }

internal static class UiSymbols
{
    internal static Bitmap CreateImage(UiSymbol kind, Color color, int size)
    {
        var image = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(image)) Draw(g, kind, new RectangleF(0, 0, size, size), color);
        return image;
    }
    internal static void Draw(Graphics graphics, UiSymbol kind, RectangleF bounds, Color color)
    {
        if (kind == UiSymbol.None || bounds.Width <= 0 || bounds.Height <= 0) return;
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(bounds.X, bounds.Y);
            graphics.ScaleTransform(bounds.Width / 24F, bounds.Height / 24F);
            using (var p = new Pen(color, 1.9F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var b = new SolidBrush(color))
            {
                switch (kind)
                {
                    case UiSymbol.Home:
                        graphics.DrawLines(p, new[] { new PointF(3, 11), new PointF(12, 3), new PointF(21, 11) });
                        graphics.DrawLines(p, new[] { new PointF(5, 10), new PointF(5, 21), new PointF(10, 21), new PointF(10, 14), new PointF(14, 14), new PointF(14, 21), new PointF(19, 21), new PointF(19, 10) }); break;
                    case UiSymbol.Pulse:
                        graphics.DrawLines(p, new[] { new PointF(2, 12), new PointF(6, 12), new PointF(9, 4), new PointF(13, 21), new PointF(16, 9), new PointF(18, 12), new PointF(22, 12) }); break;
                    case UiSymbol.User:
                        graphics.DrawEllipse(p, 8, 3, 8, 8); graphics.DrawArc(p, 4, 13, 16, 13, 180, 180); graphics.DrawLine(p, 4, 19.5F, 20, 19.5F); break;
                    case UiSymbol.Cloud:
                        graphics.DrawArc(p, 3, 10, 10, 10, 90, 180); graphics.DrawArc(p, 7, 4, 12, 13, 180, 190); graphics.DrawArc(p, 15, 10, 7, 10, 270, 180); graphics.DrawLine(p, 7, 20, 18, 20); break;
                    case UiSymbol.Tools:
                        graphics.DrawRectangle(p, 3, 8, 18, 13); graphics.DrawRectangle(p, 8, 4, 8, 4); graphics.DrawLine(p, 3, 13, 21, 13); graphics.DrawLine(p, 10, 13, 14, 13); break;
                    case UiSymbol.Settings:
                        graphics.DrawEllipse(p, 5, 5, 14, 14); graphics.DrawEllipse(p, 9, 9, 6, 6);
                        for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4; graphics.DrawLine(p, 12 + (float)Math.Cos(a) * 8, 12 + (float)Math.Sin(a) * 8, 12 + (float)Math.Cos(a) * 10, 12 + (float)Math.Sin(a) * 10); } break;
                    case UiSymbol.Info:
                        graphics.DrawEllipse(p, 3, 3, 18, 18); graphics.FillEllipse(b, 11, 6, 2, 2); graphics.DrawLine(p, 12, 11, 12, 17); break;
                    case UiSymbol.Check:
                        graphics.DrawLines(p, new[] { new PointF(5, 12), new PointF(10, 17), new PointF(19, 7) }); break;
                    case UiSymbol.Down: case UiSymbol.Up:
                        bool up = kind == UiSymbol.Up; graphics.DrawLine(p, 12, 4, 12, 20);
                        graphics.DrawLines(p, up ? new[] { new PointF(6, 10), new PointF(12, 4), new PointF(18, 10) } : new[] { new PointF(6, 14), new PointF(12, 20), new PointF(18, 14) }); break;
                    case UiSymbol.Clock:
                        graphics.DrawEllipse(p, 3, 3, 18, 18); graphics.DrawLines(p, new[] { new PointF(12, 7), new PointF(12, 12), new PointF(16, 14) }); break;
                    case UiSymbol.Laptop:
                        graphics.DrawRectangle(p, 5, 4, 14, 12); graphics.DrawLines(p, new[] { new PointF(5, 17), new PointF(2, 20), new PointF(22, 20), new PointF(19, 17) }); break;
                    case UiSymbol.Chart:
                        graphics.FillRectangle(b, 3, 12, 4, 9); graphics.FillRectangle(b, 10, 4, 4, 17); graphics.FillRectangle(b, 17, 8, 4, 13); break;
                    case UiSymbol.File:
                        graphics.DrawLines(p, new[] { new PointF(5, 3), new PointF(15, 3), new PointF(20, 8), new PointF(20, 21), new PointF(5, 21), new PointF(5, 3) });
                        graphics.DrawLines(p, new[] { new PointF(15, 3), new PointF(15, 8), new PointF(20, 8) }); graphics.DrawLine(p, 9, 12, 16, 12); graphics.DrawLine(p, 9, 16, 16, 16); break;
                    case UiSymbol.Wrench:
                        graphics.DrawArc(p, 12, 2, 9, 9, 40, 280); graphics.DrawLine(p, 13, 10, 4, 20); graphics.DrawLine(p, 9, 9, 2, 17); graphics.DrawLine(p, 2, 17, 4, 20); break;
                    case UiSymbol.Play: graphics.FillPolygon(b, new[] { new PointF(7, 3), new PointF(21, 12), new PointF(7, 21) }); break;
                    case UiSymbol.Link:
                        graphics.DrawArc(p, 2, 10, 13, 10, 35, 285); graphics.DrawArc(p, 10, 3, 12, 10, 215, 285); graphics.DrawLine(p, 8, 16, 16, 8); break;
                    case UiSymbol.Globe:
                        graphics.DrawEllipse(p, 2, 2, 20, 20); graphics.DrawEllipse(p, 8, 2, 8, 20); graphics.DrawLine(p, 3, 8, 21, 8); graphics.DrawLine(p, 3, 16, 21, 16); break;
                    case UiSymbol.Speed:
                        graphics.DrawArc(p, 3, 3, 18, 18, 140, 260); graphics.DrawLine(p, 12, 13, 17, 7); graphics.FillEllipse(b, 10, 11, 4, 4); graphics.DrawLine(p, 6, 9, 8, 10); break;
                    case UiSymbol.Copy:
                        graphics.DrawRectangle(p, 8, 7, 12, 14); graphics.DrawLines(p, new[] { new PointF(5, 17), new PointF(3, 17), new PointF(3, 3), new PointF(15, 3), new PointF(15, 5) }); break;
                    case UiSymbol.Stop: graphics.FillRectangle(b, 5, 5, 14, 14); break;
                    case UiSymbol.Wifi:
                        graphics.DrawArc(p, 1, 3, 22, 20, 220, 100); graphics.DrawArc(p, 5, 8, 14, 13, 220, 100); graphics.DrawArc(p, 9, 13, 6, 6, 220, 100); graphics.FillEllipse(b, 10, 19, 4, 4); break;
                    case UiSymbol.List:
                        for (int i = 0; i < 3; i++) { graphics.FillEllipse(b, 3, 5 + i * 6, 3, 3); graphics.DrawLine(p, 10, 6.5F + i * 6, 21, 6.5F + i * 6); } break;
                    case UiSymbol.Bulb:
                        graphics.DrawArc(p, 5, 2, 14, 15, 150, 240); graphics.DrawLine(p, 7, 13, 9, 18); graphics.DrawLine(p, 17, 13, 15, 18); graphics.DrawLine(p, 9, 18, 15, 18); graphics.DrawLine(p, 10, 21, 14, 21); break;
                    case UiSymbol.Refresh:
                        graphics.DrawArc(p, 3, 4, 18, 17, 40, 300); graphics.DrawLines(p, new[] { new PointF(17, 2), new PointF(21, 5), new PointF(16, 7) }); break;
                    case UiSymbol.External:
                        graphics.DrawLines(p, new[] { new PointF(11, 4), new PointF(4, 4), new PointF(4, 21), new PointF(21, 21), new PointF(21, 14) }); graphics.DrawLine(p, 11, 13, 21, 3); graphics.DrawLine(p, 15, 3, 21, 3); graphics.DrawLine(p, 21, 3, 21, 9); break;
                    case UiSymbol.Router:
                        graphics.DrawRectangle(p, 3, 13, 18, 7); graphics.DrawLine(p, 7, 12, 5, 3); graphics.DrawLine(p, 17, 12, 19, 3); graphics.FillEllipse(b, 6, 16, 2, 2); graphics.FillEllipse(b, 10, 16, 2, 2); break;
                    case UiSymbol.Game:
                        using (var body = CardPanel.MakePath(new Rectangle(2, 7, 20, 12), 6)) graphics.DrawPath(p, body);
                        graphics.DrawLine(p, 5, 13, 10, 13); graphics.DrawLine(p, 7.5F, 10.5F, 7.5F, 15.5F);
                        graphics.FillEllipse(b, 14.5F, 10.5F, 2.5F, 2.5F); graphics.FillEllipse(b, 17, 13.5F, 2.5F, 2.5F); break;
                    case UiSymbol.Shield:
                        graphics.DrawLines(p, new[] { new PointF(12, 2), new PointF(21, 6), new PointF(19, 16), new PointF(12, 22), new PointF(5, 16), new PointF(3, 6), new PointF(12, 2) }); break;
                }
            }
        }
        finally { graphics.Restore(state); }
    }
}

internal sealed class UiIcon : Control
{
    internal UiSymbol Symbol;
    internal bool Disc = true;
    internal Color Tint = SurfaceColor.Blue;
    internal UiIcon() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; TabStop = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Rounded tile rather than a circle: reads as a deliberate icon plate.
        if (Disc)
            using (var path = CardPanel.MakePath(new Rectangle(0, 0, Width - 1, Height - 1), Math.Max(4, (int)(Width * .28F))))
            using (var brush = new SolidBrush(Color.FromArgb(22, Tint))) e.Graphics.FillPath(brush, path);
        float inset = Disc ? Width * .25F : 1;
        UiSymbols.Draw(e.Graphics, Symbol, new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset), Tint);
    }
}

internal sealed class StatusEmblem : Label
{
    private readonly UiTransition pulse;
    private string shown;
    internal StatusEmblem()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        pulse = new UiTransition(this); pulse.Snap(1);
    }
    // One ring answers each real state change (online → needs login, etc.);
    // nothing loops while the state stays the same.
    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (pulse == null) return;
        if (shown != null && shown != Text && IsHandleCreated) { pulse.Snap(0); pulse.To(1, 720); }
        shown = Text;
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (var brush = new SolidBrush(UiSurface.Behind(this))) e.Graphics.FillRectangle(brush, ClientRectangle);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        bool good = Text == "✓";
        Color color = good ? SurfaceColor.Green : ForeColor;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float size = Math.Min(Width, Height) - 1;
        var outer = new RectangleF((Width - size) / 2F, (Height - size) / 2F, size, size);
        using (var halo = new SolidBrush(Color.FromArgb(24, color))) g.FillEllipse(halo, outer);
        var face = RectangleF.Inflate(outer, -size * .17F, -size * .17F);
        double v = pulse == null ? 1 : pulse.Value;
        if (v < 1)
        {
            float grow = (float)(size * .17F * v);
            using (var ring = new Pen(Color.FromArgb((int)(110 * (1 - v)), color), 2F)) g.DrawEllipse(ring, RectangleF.Inflate(face, grow, grow));
        }
        using (var brush = new SolidBrush(color)) g.FillEllipse(brush, face);
        // A faint inner edge keeps the solid disc from looking stamped-on.
        using (var edge = new Pen(Color.FromArgb(36, Color.Black), 1F)) g.DrawEllipse(edge, RectangleF.Inflate(face, -.5F, -.5F));
        float glyph = face.Width * .5F;
        UiSymbols.Draw(g, good ? UiSymbol.Check : Text == "!" ? UiSymbol.Info : UiSymbol.Refresh,
            new RectangleF(face.X + (face.Width - glyph) / 2F, face.Y + (face.Height - glyph) / 2F, glyph, glyph), Color.White);
    }
    protected override void Dispose(bool disposing) { if (disposing && pulse != null) pulse.Dispose(); base.Dispose(disposing); }
}

internal sealed class SurfacePanel : BufferedPanel
{
    internal Color Fill = SurfaceColor.Pale;
    internal Color Edge = SurfaceColor.Border;
    internal int Radius = 8;
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = CardPanel.MakePath(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), Radius))
        using (var b = new SolidBrush(Fill)) using (var p = new Pen(Edge)) { e.Graphics.FillPath(b, path); e.Graphics.DrawPath(p, path); }
        base.OnPaint(e);
    }
}

internal sealed class StatePill : Label
{
    internal Rectangle TextBounds { get { return new Rectangle(27, 0, Math.Max(0, Width - 32), Height); } }
    internal string DisplayText
    {
        get { using (Graphics graphics = CreateGraphics()) return DisplayValue(graphics); }
    }
    private string DisplayValue(IDeviceContext graphics)
    {
            string value = Text.TrimStart('●', ' ');
            if (Width < 145) {
                if (value.Contains("适配器已连接")) value = "已连接";
                else if (value.Contains("连接未就绪")) value = "未就绪";
                else if (value.Contains("读取")) value = "读取中";
            }
            if (value.StartsWith("后台监控") && TextRenderer.MeasureText(graphics, value, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width > TextBounds.Width)
                value = value.Substring(4);
            if (value.Contains("待检测") && TextRenderer.MeasureText(graphics, value, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width > TextBounds.Width)
                value = "待检测";
            return value;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = CardPanel.MakePath(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), Height / 2))
        using (var brush = new SolidBrush(Color.FromArgb(18, ForeColor))) e.Graphics.FillPath(brush, path);
        string value = DisplayValue(e.Graphics);
        using (var dot = new SolidBrush(ForeColor)) e.Graphics.FillEllipse(dot, 12, Height / 2 - 4, 8, 8);
        TextRenderer.DrawText(e.Graphics, value, Font, TextBounds, ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}

internal sealed class TimelineLabel : Label
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (String.IsNullOrWhiteSpace(Text)) return;
        string value = Text.TrimStart('●', ' ');
        Color dot = value.Contains("成功") || value.Contains("在线") ? SurfaceColor.Green : ForeColor.ToArgb() == UiColor.Ink.ToArgb() ? SurfaceColor.Blue : ForeColor;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = new Pen(SurfaceColor.Border, 1.5F)) e.Graphics.DrawLine(p, 7, 0, 7, Height);
        using (var halo = new SolidBrush(Color.FromArgb(36, dot))) e.Graphics.FillEllipse(halo, 1, Height / 2 - 6, 12, 12);
        using (var b = new SolidBrush(dot)) e.Graphics.FillEllipse(b, 4, Height / 2 - 3, 6, 6);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        if (value.Length > 12 && value[2] == ':' && value[5] == ':')
        {
            TextRenderer.DrawText(e.Graphics, value.Substring(0, 8), Font, new Rectangle(28, 0, 78, Height), SurfaceColor.Muted, flags);
            TextRenderer.DrawText(e.Graphics, value.Substring(8).Trim(), Font, new Rectangle(114, 0, Math.Max(0, Width - 114), Height), ForeColor, flags);
        }
        else TextRenderer.DrawText(e.Graphics, value, Font, new Rectangle(28, 0, Math.Max(0, Width - 28), Height), ForeColor, flags);
    }
}

internal sealed class MetricValueLabel : Label
{
    private Font unitFont, textFont;
    private float unitSize;
    protected override void OnPaint(PaintEventArgs e)
    {
        string value = Text.Replace("延迟", "").TrimStart('↓', '↑', ' ').Trim();
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        int split = value.LastIndexOf(' ');
        if (!UiFont.IsAscii(value) || split <= 0)
        {
            // Status words such as “不可达” stay in the UI face, a step smaller.
            Font face = Font;
            if (!UiFont.IsAscii(value))
            {
                if (textFont == null || Math.Abs(textFont.Size - Font.Size * .78F) > .1F)
                { if (textFont != null) textFont.Dispose(); textFont = new Font(UiFont.Family, Font.Size * .78F, FontStyle.Bold); }
                face = textFont;
            }
            TextRenderer.DrawText(e.Graphics, value, face, ClientRectangle, ForeColor, flags | TextFormatFlags.EndEllipsis);
            return;
        }
        // Figure large, unit small and quiet: “12 ms”, “3.4 MB/s”.
        string number = value.Substring(0, split), unit = value.Substring(split + 1);
        if (unitFont == null || unitSize != Font.Size)
        { if (unitFont != null) unitFont.Dispose(); unitSize = Font.Size; unitFont = new Font(UiFont.Family, Math.Max(8.5F, Font.Size * .5F)); }
        Size numberSize = TextRenderer.MeasureText(e.Graphics, number, Font, Size.Empty, flags);
        TextRenderer.DrawText(e.Graphics, number, Font, new Rectangle(0, 0, Math.Min(Width, numberSize.Width), Height), ForeColor, flags);
        int baselineShift = (int)Math.Round(Font.Height * .28F);
        TextRenderer.DrawText(e.Graphics, unit, unitFont, new Rectangle(numberSize.Width + 4, baselineShift, Math.Max(0, Width - numberSize.Width - 4), Height - baselineShift),
            SurfaceColor.Muted, flags | TextFormatFlags.EndEllipsis);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { if (unitFont != null) unitFont.Dispose(); if (textFont != null) textFont.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class QualityValueLabel : Label
{
    internal string Caption;
    internal UiSymbol Symbol;
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = CardPanel.MakePath(new Rectangle(0, 0, Width - 1, Height - 1), 8))
        using (var brush = new SolidBrush(SurfaceColor.Pale)) using (var pen = new Pen(SurfaceColor.Border))
        { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
        UiSymbols.Draw(e.Graphics, Symbol, new RectangleF(14, (Height - 22) / 2F, 22, 22), ForeColor);
        TextRenderer.DrawText(e.Graphics, Caption, Font, new Rectangle(48, 0, 88, Height), SurfaceColor.Ink, TextFormatFlags.VerticalCenter);
        string value = Text.Replace(Caption, "").Trim();
        TextRenderer.DrawText(e.Graphics, value, Font, new Rectangle(136, 0, Math.Max(1, Width - 146), Height), SurfaceColor.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
}

internal sealed class ConnectionRoute : Control
{
    internal bool Verified;
    internal ConnectionRoute() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); BackColor = Color.White; TabStop = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        int size = Math.Min(34, Height - 4); float y = (Height - size) / 2F;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = new Pen(Verified ? Color.FromArgb(120, SurfaceColor.Green) : SurfaceColor.ControlHover, 1.6F) { DashStyle = DashStyle.Dot })
            e.Graphics.DrawLine(p, size + 6, Height / 2, Width - size - 6, Height / 2);
        UiSymbols.Draw(e.Graphics, UiSymbol.Laptop, new RectangleF(0, y, size, size), SurfaceColor.Faint);
        UiSymbols.Draw(e.Graphics, UiSymbol.Globe, new RectangleF(Width - size, y, size, size), SurfaceColor.Faint);
        using (var b = new SolidBrush(Verified ? SurfaceColor.GreenTint : SurfaceColor.BlueTint)) e.Graphics.FillEllipse(b, (Width - size - 10) / 2F, y - 5, size + 10, size + 10);
        UiSymbols.Draw(e.Graphics, Verified ? UiSymbol.Check : UiSymbol.Link, new RectangleF((Width - size) / 2F + size * .2F, y + size * .2F, size * .6F, size * .6F), Verified ? SurfaceColor.Green : SurfaceColor.Blue);
    }
}

internal sealed class SampleSpark : Control
{
    private readonly List<double> samples = new List<double>();
    internal Color Line = SurfaceColor.Blue;
    internal int SampleCount { get { return samples.Count; } }
    internal SampleSpark() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); BackColor = SurfaceColor.Pale; }
    internal void Reset() { samples.Clear(); Invalidate(); }
    internal void Add(double rate) { samples.Add(Math.Max(0, rate)); if (samples.Count > 80) samples.RemoveAt(0); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (samples.Count < 2) return;
        double max = Math.Max(.001, samples.Max());
        var points = samples.Select((v, i) => new PointF(2 + i * (Width - 4F) / (samples.Count - 1), Height - 3 - (float)(v / max) * (Height - 8))).ToArray();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new GraphicsPath()) using (var brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(40, Line), Color.FromArgb(2, Line), 90))
        using (var pen = new Pen(Line, 1.5F))
        {
            fill.AddLines(points); fill.AddLine(points.Last(), new PointF(Width - 2, Height)); fill.AddLine(new PointF(2, Height), points[0]);
            e.Graphics.FillPath(brush, fill); e.Graphics.DrawLines(pen, points);
        }
    }
}

// Native dropdown kept for keyboard, accessibility and the existing API; only
// the closed face and the list rows are drawn to match the rest of the app.
internal sealed class UiCombo : ComboBox
{
    private bool hot;
    internal UiCombo()
    {
        DropDownStyle = ComboBoxStyle.DropDownList; DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 26; MaxDropDownItems = 10; IntegralHeight = false;
        Font = new Font(UiFont.Family, 10f); ForeColor = SurfaceColor.Ink; BackColor = Color.White;
        Cursor = Cursors.Hand;
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnDropDown(EventArgs e) { base.OnDropDown(e); Invalidate(); }
    protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool selected = !edit && (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? SurfaceColor.BlueTint : Color.White)) e.Graphics.FillRectangle(back, e.Bounds);
        bool current = e.Index == SelectedIndex;
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
            new Rectangle(e.Bounds.X + 12, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 20), e.Bounds.Height),
            selected || current ? SurfaceColor.BlueHover : SurfaceColor.Ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
    protected override void WndProc(ref Message m)
    {
        const int WM_PAINT = 0x000F, WM_PRINTCLIENT = 0x0318;
        if (m.Msg == WM_PAINT || m.Msg == WM_PRINTCLIENT)
        {
            if (m.WParam != IntPtr.Zero)
            {
                using (Graphics g = Graphics.FromHdc(m.WParam)) PaintFace(g);
                if (m.Msg == WM_PAINT) ValidateRect(m.HWnd, IntPtr.Zero);
                m.Result = IntPtr.Zero; return;
            }
            PaintStruct ps;
            IntPtr dc = BeginPaint(m.HWnd, out ps);
            try
            {
                using (Graphics target = Graphics.FromHdc(dc))
                using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(target, ClientRectangle))
                { PaintFace(buffer.Graphics); buffer.Render(target); }
            }
            finally { EndPaint(m.HWnd, ref ps); }
            m.Result = IntPtr.Zero; return;
        }
        base.WndProc(ref m);
    }
    private void PaintFace(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var behind = new SolidBrush(UiSurface.Behind(this))) g.FillRectangle(behind, ClientRectangle);
        var face = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        bool open = DroppedDown, focus = Focused;
        Color fill = !Enabled ? SurfaceColor.Disabled : hot && !open ? Color.FromArgb(250, 251, 253) : Color.White;
        Color edge = !Enabled ? SurfaceColor.Border : open || focus ? SurfaceColor.Blue : hot ? SurfaceColor.ControlHover : SurfaceColor.Control;
        using (var path = CardPanel.MakePath(face, 7))
        using (var brush = new SolidBrush(fill)) using (var pen = new Pen(edge)) { g.FillPath(brush, path); g.DrawPath(pen, path); }
        if ((open || focus) && Enabled)
            using (var ring = CardPanel.MakePath(Rectangle.Inflate(face, -1, -1), 6))
            using (var pen = new Pen(Color.FromArgb(70, SurfaceColor.Blue))) g.DrawPath(pen, ring);
        string text = SelectedIndex >= 0 && SelectedIndex < Items.Count ? GetItemText(Items[SelectedIndex]) : "";
        TextRenderer.DrawText(g, text, Font, new Rectangle(12, 0, Math.Max(1, Width - 44), Height), Enabled ? SurfaceColor.Ink : SurfaceColor.DisabledText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        float cx = Width - 18, cy = Height / 2F, d = open ? -1 : 1;
        using (var pen = new Pen(Enabled ? (open || hot ? SurfaceColor.Ink : SurfaceColor.Muted) : SurfaceColor.DisabledText, 1.6F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(pen, new[] { new PointF(cx - 4.5F, cy - 2.2F * d), new PointF(cx, cy + 2.3F * d), new PointF(cx + 4.5F, cy - 2.2F * d) });
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc; public int Erase; public int Left, Top, Right, Bottom; public int Restore, IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);
}

internal sealed class UiCheck : CheckBox
{
    private readonly UiTransition mark;
    private bool hot;
    internal UiCheck()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        mark = new UiTransition(this);
        Cursor = Cursors.Hand; ForeColor = SurfaceColor.Ink; Font = new Font(UiFont.Family, 10f);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); mark.Snap(Checked ? 1 : 0); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); if (mark != null) mark.To(Checked ? 1 : 0, 140); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }
    public override Size GetPreferredSize(Size proposed)
    {
        Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        return new Size(text.Width + 34, Math.Max(26, text.Height + 8));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var behind = new SolidBrush(UiSurface.Behind(this))) g.FillRectangle(behind, ClientRectangle);
        double v = mark == null ? (Checked ? 1 : 0) : mark.Value;
        const int box = 18; int y = (Height - box) / 2;
        var r = new Rectangle(1, y, box, box);
        Color accent = Enabled ? SurfaceColor.Blue : SurfaceColor.DisabledText;
        Color fill = UiMotion.Mix(Enabled ? Color.White : SurfaceColor.Disabled, accent, v);
        Color edge = v > .5 ? accent : hot && Enabled ? SurfaceColor.Muted : SurfaceColor.ControlHover;
        using (var path = CardPanel.MakePath(r, 5)) using (var brush = new SolidBrush(fill)) using (var pen = new Pen(edge, 1.2F))
        { g.FillPath(brush, path); g.DrawPath(pen, path); }
        if (v > .05)
            using (var pen = new Pen(Color.FromArgb((int)(255 * Math.Min(1, v * 1.4)), Color.White), 2F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(r.X + 4.5F, r.Y + 9.5F), new PointF(r.X + 7.8F, r.Y + 12.6F), new PointF(r.X + 13.6F, r.Y + 5.8F) });
        if (Focused && ShowFocusCues)
            using (var ring = CardPanel.MakePath(Rectangle.Inflate(r, 2, 2), 7)) using (var pen = new Pen(Color.FromArgb(110, SurfaceColor.Blue), 2F)) g.DrawPath(pen, ring);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(box + 10, 0, Math.Max(1, Width - box - 10), Height), Enabled ? ForeColor : SurfaceColor.DisabledText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
    protected override void Dispose(bool disposing) { if (disposing && mark != null) mark.Dispose(); base.Dispose(disposing); }
}

internal sealed class UiProgress : ProgressBar
{
    internal UiProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var behind = new SolidBrush(UiSurface.Behind(this))) g.FillRectangle(behind, ClientRectangle);
        int h = Math.Max(4, Math.Min(8, Height - 2)); int y = (Height - h) / 2;
        var track = new Rectangle(0, y, Math.Max(1, Width - 1), h);
        using (var path = CardPanel.MakePath(track, h / 2)) using (var brush = new SolidBrush(SurfaceColor.Border)) g.FillPath(brush, path);
        double ratio = Maximum <= Minimum ? 0 : (Value - Minimum) / (double)(Maximum - Minimum);
        int width = (int)Math.Round(track.Width * Math.Max(0, Math.Min(1, ratio)));
        if (width >= h)
            using (var path = CardPanel.MakePath(new Rectangle(track.X, track.Y, width, h), h / 2)) using (var brush = new SolidBrush(SurfaceColor.Blue)) g.FillPath(brush, path);
    }
}

// Window controls drawn as vector glyphs; keeps the Label contract used by tests.
internal sealed class CaptionGlyph : Label
{
    internal CaptionGlyph() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Color back = BackColor.A == 255 ? BackColor : UiSurface.Behind(this);
        using (var brush = new SolidBrush(back)) g.FillRectangle(brush, ClientRectangle);
        g.SmoothingMode = SmoothingMode.None;
        float cx = Width / 2F, cy = Height / 2F;
        using (var pen = new Pen(ForeColor, 1F))
        {
            if (Name == "WindowClose")
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5); g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
            }
            else if (Name == "WindowMaximize")
            {
                Form window = FindForm();
                if (window != null && window.WindowState == FormWindowState.Maximized)
                {
                    g.DrawRectangle(pen, (int)cx - 5, (int)cy - 3, 8, 8);
                    g.DrawLines(pen, new[] { new PointF((int)cx - 3, (int)cy - 3), new PointF((int)cx - 3, (int)cy - 5), new PointF((int)cx + 5, (int)cy - 5), new PointF((int)cx + 5, (int)cy + 3), new PointF((int)cx + 3, (int)cy + 3) });
                }
                else g.DrawRectangle(pen, (int)cx - 5, (int)cy - 5, 10, 10);
            }
            else g.DrawLine(pen, (int)cx - 5, (int)cy, (int)cx + 5, (int)cy);
        }
    }
}

// A text link that answers the pointer: darker on hover, underline on press.
internal sealed class UiLink : Label
{
    private bool hot, down;
    internal UiLink()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand; ForeColor = SurfaceColor.Blue; BackColor = Color.Transparent; TabStop = false;
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        bool right = TextAlign == ContentAlignment.MiddleRight || TextAlign == ContentAlignment.TopRight || TextAlign == ContentAlignment.BottomRight;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        Size size = TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty, flags);
        int textLeft = right ? Math.Max(0, Width - 8 - size.Width) : 8;
        if (hot)
        {
            var pill = new Rectangle(Math.Max(0, textLeft - 8), 1, Math.Min(Width - 1, size.Width + 16), Math.Max(1, Height - 3));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = CardPanel.MakePath(pill, 6))
            using (var brush = new SolidBrush(down ? Color.FromArgb(218, 229, 252) : SurfaceColor.BlueTint)) e.Graphics.FillPath(brush, path);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(textLeft, 0, Math.Max(1, Width - textLeft), Height), hot ? SurfaceColor.BluePressed : ForeColor, flags);
    }
}
