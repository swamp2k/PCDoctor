using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PCDoctor.Core;

namespace PCDoctor.App;

/// <summary>A simple line chart of a recording: CPU, GPU, RAM and disk from 0 to 100 % over time.</summary>
public sealed class PerfChart : Control
{
    public static readonly IBrush CpuBrush = Brush.Parse("#FF8A3D");
    public static readonly IBrush GpuBrush = Brush.Parse("#4F9DFF");
    public static readonly IBrush RamBrush = Brush.Parse("#A78BFA");
    public static readonly IBrush DiskBrush = Brush.Parse("#2DD4BF");
    public static readonly IBrush NetBrush = Brush.Parse("#F472B6");

    static readonly IPen GridPen = new Pen(Brush.Parse("#263043"), 1);
    static readonly IBrush LabelBrush = Brush.Parse("#7A8499");

    IReadOnlyList<PerfSample> _samples = [];

    public IReadOnlyList<PerfSample> Samples
    {
        get => _samples;
        set { _samples = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext ctx)
    {
        const double left = 40, bottom = 20, top = 6, right = 8;
        double w = Bounds.Width - left - right, h = Bounds.Height - top - bottom;
        if (w <= 10 || h <= 10)
            return;

        // At least one minute wide so the first seconds do not fill the whole chart.
        double span = Math.Max(60, _samples.Count == 0 ? 0 : _samples[^1].Seconds);
        Point P(double sec, double pct) => new(left + w * sec / span, top + h * (1 - pct / 100));

        foreach (int pct in new[] { 0, 25, 50, 75, 100 })
        {
            ctx.DrawLine(GridPen, P(0, pct), P(span, pct));
            Label(ctx, $"{pct} %", new Point(2, P(0, pct).Y - 7));
        }
        int step = span <= 120 ? 30 : span <= 300 ? 60 : 180;
        for (int s = 0; s <= span; s += step)
            Label(ctx, $"{s / 60}:{s % 60:00}", new Point(P(s, 0).X - 10, top + h + 3));

        Line(ctx, DiskBrush, s => s.Disk);
        Line(ctx, RamBrush, s => s.Ram);
        Line(ctx, GpuBrush, s => s.Gpu);
        Line(ctx, CpuBrush, s => s.Cpu);

        void Line(DrawingContext c, IBrush brush, Func<PerfSample, double?> value)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool open = false;
                foreach (var s in _samples)
                {
                    double? v = value(s);
                    if (v is null) { if (open) g.EndFigure(false); open = false; continue; }
                    Point p = P(s.Seconds, v.Value);
                    if (!open) { g.BeginFigure(p, false); open = true; }
                    else g.LineTo(p);
                }
                if (open) g.EndFigure(false);
            }
            c.DrawGeometry(null, new Pen(brush, 2, lineJoin: PenLineJoin.Round), geo);
        }
    }

    static void Label(DrawingContext ctx, string text, Point at) =>
        ctx.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, LabelBrush), at);
}
