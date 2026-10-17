using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PCDoctor.Core;

namespace PCDoctor.App;

/// <summary>A small chart of the network: ping (ms) and traffic (Mbit/s) over the last two minutes. Each line has its own scale.</summary>
public sealed class NetChart : Control
{
    static readonly IPen GridPen = new Pen(Brush.Parse("#263043"), 1);
    const int Window = 120;

    IReadOnlyList<PerfSample> _samples = [];

    public IReadOnlyList<PerfSample> Samples
    {
        get => _samples;
        set { _samples = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 10 || h <= 10)
            return;
        ctx.DrawLine(GridPen, new Point(0, h - 1), new Point(w, h - 1));
        ctx.DrawLine(GridPen, new Point(0, h / 2), new Point(w, h / 2));
        var last = _samples.Count > Window ? _samples.Skip(_samples.Count - Window).ToList() : _samples.ToList();
        if (last.Count < 2)
            return;
        double first = last[0].Seconds, span = Math.Max(Window, last[^1].Seconds - first);

        Line(ctx, PerfChart.NetBrush, last, s => s.PingMs, 100, first, span, w, h);
        Line(ctx, PerfChart.DiskBrush, last, s => s.NetMbps, 5, first, span, w, h);
    }

    /// <summary>One line; the scale is at least <paramref name="minScale"/> and grows when the values do.</summary>
    static void Line(DrawingContext ctx, IBrush brush, List<PerfSample> samples, Func<PerfSample, double?> value, double minScale, double first, double span, double w, double h)
    {
        double scale = Math.Max(minScale, samples.Max(s => value(s) ?? 0));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool open = false;
            foreach (var s in samples)
            {
                if (value(s) is not { } v) continue;
                var p = new Point(w * (s.Seconds - first) / span, 3 + (h - 6) * (1 - v / scale));
                if (!open) { g.BeginFigure(p, false); open = true; }
                else g.LineTo(p);
            }
            if (open) g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(brush, 2, lineJoin: PenLineJoin.Round), geo);
    }
}
