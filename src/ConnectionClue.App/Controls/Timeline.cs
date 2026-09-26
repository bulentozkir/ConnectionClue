using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ConnectionClue.Analysis;
using ConnectionClue.Presentation.Localization;

namespace ConnectionClue.App.Controls;

/// <summary>Colour-blind-safe (Okabe–Ito based) chart colours from the current theme; system colours in Windows high contrast.</summary>
internal sealed record ChartPalette(Brush Normal, Brush Over, Brush Ink, Brush Grid, Brush NoAnswerTint, Brush NoAnswerHatch, Brush Marker)
{
    public static ChartPalette For(FrameworkElement e)
    {
        var ink = e.TryFindResource("TextFillColorPrimaryBrush") as Brush ?? SystemColors.WindowTextBrush;
        if (ThemeManager.Palette is not { } theme)
        {
            return new(SystemColors.HighlightBrush, SystemColors.HotTrackBrush, SystemColors.WindowTextBrush,
                SystemColors.GrayTextBrush, Brushes.Transparent, Hatch(SystemColors.WindowTextColor), SystemColors.WindowTextBrush);
        }
        var c = theme.Chart;
        var none = (Color)ColorConverter.ConvertFromString(c.NoAnswer);
        return new(ThemeManager.Brush(c.Normal), ThemeManager.Brush(c.Over), ink, ThemeManager.Brush(c.Grid),
            // High contrast keeps the hatch on a plain background; the tint would lower its contrast.
            Solid(Color.FromArgb(theme.IsHighContrast ? (byte)0 : (byte)60, none.R, none.G, none.B)), Hatch(none), ThemeManager.Brush(c.Marker));
    }

    private static Brush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Brush Hatch(Color c)
    {
        var pen = new Pen(new SolidColorBrush(c), 1.6);
        var drawing = new GeometryDrawing(null, pen, new LineGeometry(new Point(0, 6), new Point(6, 0)));
        var b = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 6, 6),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        b.Freeze();
        return b;
    }
}

/// <summary>
/// One path step as columns over time; each row has its own scale (small multiples), so different quantities never
/// share an axis. Unanswered checks are full-height hatched columns, values above the user's limit use a second colour,
/// and values beyond the scale get a ▲ cap. Shapes only, so right-to-left mirroring is automatic.
/// </summary>
public sealed class SampleStrip : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = Register(nameof(Samples), typeof(IReadOnlyList<Sample>), null);
    public static readonly DependencyProperty VersionProperty = Register(nameof(Version), typeof(int), 0);
    public static readonly DependencyProperty DurationSecondsProperty = Register(nameof(DurationSeconds), typeof(double), 120.0);
    public static readonly DependencyProperty ScaleMaxMsProperty = Register(nameof(ScaleMaxMs), typeof(double), 10.0);
    public static readonly DependencyProperty LimitMsProperty = Register(nameof(LimitMs), typeof(double), 0.0);
    public static readonly DependencyProperty MarkersProperty = Register(nameof(Markers), typeof(IReadOnlyList<double>), null);

    public IReadOnlyList<Sample>? Samples { get => (IReadOnlyList<Sample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public double DurationSeconds { get => (double)GetValue(DurationSecondsProperty); set => SetValue(DurationSecondsProperty, value); }
    public double ScaleMaxMs { get => (double)GetValue(ScaleMaxMsProperty); set => SetValue(ScaleMaxMsProperty, value); }
    public double LimitMs { get => (double)GetValue(LimitMsProperty); set => SetValue(LimitMsProperty, value); }

    /// <summary>Seconds at which the user marked lag; drawn as dashed lines with a flag, on top of the bars.</summary>
    public IReadOnlyList<double>? Markers { get => (IReadOnlyList<double>?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }

    private static DependencyProperty Register(string name, Type type, object? fallback) =>
        DependencyProperty.Register(name, type, typeof(SampleStrip),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    // Follows the pointer and names the bar under it, so values can be compared exactly.
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative };

    public SampleStrip()
    {
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetBetweenShowDelay(this, 0);
        ToolTipService.SetShowDuration(this, 60_000);
        ThemeRepaint.Attach(this);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        (_tip.HorizontalOffset, _tip.VerticalOffset) = (position.X + 14, position.Y + 18);
        double pxPerSecond = ActualWidth / Math.Max(DurationSeconds, 1);
        if (Samples is not { Count: > 0 } samples)
        {
            _tip.Visibility = Visibility.Collapsed;
            return;
        }
        var nearest = samples.MinBy(s => Math.Abs(s.Seconds * pxPerSecond - position.X))!;
        var f = CultureInfo.CurrentCulture;
        string seconds = nearest.Seconds.ToString("0", f);
        _tip.Content = nearest.Milliseconds is { } ms
            ? string.Format(f, Localizer.Default.Get("Chart_BarValue"), seconds, ms.ToString(ms < 10 ? "0.0" : "0", f))
            : string.Format(f, Localizer.Default.Get("Chart_BarNoAnswer"), seconds);
        _tip.Visibility = Visibility.Visible;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        var p = ChartPalette.For(this);
        var solid = Frozen(new Pen(p.Grid, 1));
        var dotted = Frozen(new Pen(p.Grid, 1) { DashStyle = new DashStyle([2, 3], 0) });
        // Value grid at 0, ¼, ½, ¾ and the top of the scale (labels: ScaleLabels), time grid at the axis steps.
        for (int k = 0; k < ScaleLabels.Steps; k++)
        {
            double y = Math.Round(h * k / ScaleLabels.Steps) + 0.5;
            dc.DrawLine(dotted, new Point(0, y), new Point(w, y));
        }
        dc.DrawLine(solid, new Point(0, h - 0.5), new Point(w, h - 0.5));
        double duration = Math.Max(DurationSeconds, 1), pxPerSecond = w / duration, step = TimeAxis.Step(duration);
        for (double t = step; t < duration - 1e-6; t += step)
        {
            double x = Math.Round(t * pxPerSecond) + 0.5;
            dc.DrawLine(dotted, new Point(x, 0), new Point(x, h));
        }
        if (Samples is { Count: > 0 } samples) DrawSamples(dc, p, samples, w, h, pxPerSecond);
        foreach (var t in Markers ?? [])
        {
            double x = Math.Round(Math.Clamp(t * pxPerSecond, 1, w - 1)) + 0.5;
            dc.DrawLine(Frozen(new Pen(p.Marker, 1.5) { DashStyle = new DashStyle([3, 2], 0) }), new Point(x, 0), new Point(x, h));
            Flag(dc, p.Marker, x);
        }
    }

    private void DrawSamples(DrawingContext dc, ChartPalette p, IReadOnlyList<Sample> samples, double w, double h, double pxPerSecond)
    {
        double scale = Math.Max(ScaleMaxMs, 1);
        double bar = Math.Clamp(TypicalSpacing(samples) * pxPerSecond * 0.7, 2, 10);
        foreach (var s in samples)
        {
            double x = Math.Clamp(s.Seconds * pxPerSecond - bar / 2, 0, w - bar);
            if (s.Milliseconds is not { } ms)
            {
                var column = new Rect(x, 0, bar, h);
                dc.DrawRectangle(p.NoAnswerTint, null, column);
                dc.DrawRectangle(p.NoAnswerHatch, null, column);
                continue;
            }
            double height = Math.Max(2, Math.Min(ms, scale) / scale * h);
            dc.DrawRectangle(LimitMs > 0 && ms > LimitMs ? p.Over : p.Normal, null, new Rect(x, h - height, bar, height));
            if (ms > scale) Cap(dc, p.Ink, x + bar / 2);
        }

        if (LimitMs > 0 && LimitMs < scale)
        {
            double y = Math.Round(h - LimitMs / scale * h) + 0.5;
            dc.DrawLine(Frozen(new Pen(p.Over, 1.5) { DashStyle = DashStyles.Dash }), new Point(0, y), new Point(w, y));
        }
    }

    /// <summary>Pennant at the top of a lag marker, so the mark is a shape as well as a colour.</summary>
    internal static void Flag(DrawingContext dc, Brush brush, double x)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x, 0), true, true);
            c.LineTo(new Point(x + 7, 3), true, false);
            c.LineTo(new Point(x, 6), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
    }

    private static void Cap(DrawingContext dc, Brush brush, double cx)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx, 0), true, true);
            c.LineTo(new Point(cx - 4, 6), true, false);
            c.LineTo(new Point(cx + 4, 6), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
    }

    private static double TypicalSpacing(IReadOnlyList<Sample> s)
    {
        if (s.Count < 2) return 1;
        var d = new double[s.Count - 1];
        for (int i = 1; i < s.Count; i++) d[i - 1] = s[i].Seconds - s[i - 1].Seconds;
        Array.Sort(d);
        return Math.Max(d[d.Length / 2], 0.5);
    }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}

/// <summary>Time labels under the strips. Text must not mirror, so the axis stays left-to-right and places labels for RTL parents.</summary>
public sealed class TimeAxis : FrameworkElement
{
    public static readonly DependencyProperty DurationSecondsProperty = DependencyProperty.Register(nameof(DurationSeconds),
        typeof(double), typeof(TimeAxis), new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public TimeAxis()
    {
        FlowDirection = FlowDirection.LeftToRight;
        ThemeRepaint.Attach(this);
    }

    public double DurationSeconds { get => (double)GetValue(DurationSecondsProperty); set => SetValue(DurationSecondsProperty, value); }

    /// <summary>Label and grid spacing: about five steps per check, on round seconds.</summary>
    public static double Step(double duration) => new[] { 1.0, 2, 5, 10, 15, 30, 60, 120, 300 }.FirstOrDefault(s => s >= duration / 5, 600);

    protected override Size MeasureOverride(Size availableSize) => new(0, 18);

    protected override void OnRender(DrawingContext dc)
    {
        bool rtl = VisualTreeHelper.GetParent(this) is FrameworkElement { FlowDirection: FlowDirection.RightToLeft };
        var brush = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? SystemColors.GrayTextBrush;
        var face = new Typeface((FontFamily)GetValue(TextElement.FontFamilyProperty), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double w = ActualWidth, duration = Math.Max(DurationSeconds, 1), dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double step = Step(duration);
        for (double t = 0; t <= duration + 1e-6; t += step)
        {
            var text = new FormattedText(string.Format(CultureInfo.CurrentCulture, "{0:0} s", t), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, face, 12, brush, dpi);
            double x = t / duration * w;
            if (rtl) x = w - x;
            dc.DrawText(text, new Point(Math.Clamp(x - text.Width / 2, 0, Math.Max(0, w - text.Width)), 2));
        }
    }
}

/// <summary>Value labels beside a strip, level with its grid lines: the scale top in ms, then ¾, ½, ¼ and 0.</summary>
public sealed class ScaleLabels : FrameworkElement
{
    public const int Steps = 4;

    public static readonly DependencyProperty ScaleMaxMsProperty = DependencyProperty.Register(nameof(ScaleMaxMs),
        typeof(double), typeof(ScaleLabels), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public ScaleLabels()
    {
        FlowDirection = FlowDirection.LeftToRight;
        ThemeRepaint.Attach(this);
    }

    public double ScaleMaxMs { get => (double)GetValue(ScaleMaxMsProperty); set => SetValue(ScaleMaxMsProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double h = ActualHeight, dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (h < 20) return;
        var brush = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? SystemColors.GrayTextBrush;
        var face = new Typeface((FontFamily)GetValue(TextElement.FontFamilyProperty), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var f = CultureInfo.CurrentCulture;
        // Fewer labels on short rows, so they never overlap; the grid lines stay.
        int every = h / Steps < 16 ? 2 : 1;
        for (int k = 0; k <= Steps; k += every)
        {
            double value = ScaleMaxMs * (Steps - k) / Steps;
            string label = k == 0 ? string.Format(f, "{0:0.#} ms", value) : value.ToString("0.#", f);
            var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 11, brush, dpi);
            double y = Math.Clamp(h * k / Steps - text.Height / 2, 0, h - text.Height);
            dc.DrawText(text, new Point(Math.Max(0, ActualWidth - text.Width - 4), y));
        }
    }
}

/// <summary>Repaints a custom-drawn element when the theme changes (its colours come from the palette, not bindings).</summary>
internal static class ThemeRepaint
{
    public static void Attach(FrameworkElement element)
    {
        void Repaint(object? sender, EventArgs e) => element.InvalidateVisual();
        element.Loaded += (_, _) => ThemeManager.Changed += Repaint;
        element.Unloaded += (_, _) => ThemeManager.Changed -= Repaint;
    }
}

/// <summary>Legend sample matching the strip's encodings.</summary>
public sealed class LegendSwatch : FrameworkElement
{
    public LegendSwatch() => ThemeRepaint.Attach(this);

    public bool NoAnswer { get; set; }

    public bool Marker { get; set; }

    protected override Size MeasureOverride(Size availableSize) => new(10, 12);

    protected override void OnRender(DrawingContext dc)
    {
        var p = ChartPalette.For(this);
        var r = new Rect(0, 0, ActualWidth, ActualHeight);
        if (Marker)
        {
            var pen = new Pen(p.Marker, 1.5) { DashStyle = new DashStyle([3, 2], 0) };
            pen.Freeze();
            dc.DrawLine(pen, new Point(1.5, 0), new Point(1.5, r.Height));
            SampleStrip.Flag(dc, p.Marker, 1.5);
            return;
        }
        if (!NoAnswer)
        {
            dc.DrawRectangle(p.Over, null, r);
            return;
        }
        dc.DrawRectangle(p.NoAnswerTint, null, r);
        dc.DrawRectangle(p.NoAnswerHatch, null, r);
    }
}
