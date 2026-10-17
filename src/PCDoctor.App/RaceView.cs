using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PCDoctor.Core;

namespace PCDoctor.App;

/// <summary>
/// "The race": one bar per part of the PC (CPU, graphics card, RAM, disk, network). The busier a part, the longer its bar
/// and the higher up it runs. The leader is the most likely reason the game stutters. Bars glide to new values.
/// </summary>
public sealed class RaceView : Control
{
    sealed class Racer(Func<string> name, IBrush brush)
    {
        public Func<string> Name { get; } = name;
        public IBrush Brush { get; } = brush;
        public double? Target;
        public double Shown;
        public double Y;
        public string Text = "";
    }

    readonly Racer[] _racers =
    [
        new(() => "CPU", PerfChart.CpuBrush),
        new(() => L.T("Grafikkort", "Graphics card", "Grafikkort", "Skjermkort", "Grafikkarte"), PerfChart.GpuBrush),
        new(() => "RAM", PerfChart.RamBrush),
        new(() => "Disk", PerfChart.DiskBrush),
        new(() => L.T("Netværk (lag)", "Network (lag)", "Nätverk (lagg)", "Nettverk (lagg)", "Netzwerk (Lag)"), PerfChart.NetBrush),
    ];

    const double RowHeight = 46, BarHeight = 26, LabelWidth = 130;
    static readonly IBrush TrackBrush = Brush.Parse("#232C3E");
    static readonly IBrush TextBrush = Brush.Parse("#E6EAF2");
    static readonly IBrush MutedBrush = Brush.Parse("#9AA4B8");
    static readonly IBrush LeaderBrush = Brush.Parse("#FF8A3D");

    DispatcherTimer? _timer;

    public RaceView()
    {
        MinHeight = RowHeight * _racers.Length + 8;
        for (int i = 0; i < _racers.Length; i++)
            _racers[i].Y = i * RowHeight;
    }

    /// <summary>New values, 0-100 (null = this part has no reading). The text is shown on the bar instead of the percentage.</summary>
    public void Update(double? cpu, double? gpu, double? ram, double? disk, double? networkLag, string? networkText = null)
    {
        double?[] values = [cpu, gpu, ram, disk, networkLag];
        for (int i = 0; i < _racers.Length; i++)
        {
            _racers[i].Target = values[i];
            _racers[i].Text = values[i] is { } v ? (i == 4 && networkText is not null ? networkText : $"{v:0} %") : "–";
        }
    }

    /// <summary>The bars drop back to the start line.</summary>
    public void Reset() => Update(null, null, null, null, null);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Step());
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>One animation frame: values and positions move a bit closer to where they should be.</summary>
    void Step()
    {
        var order = Order();
        for (int rank = 0; rank < order.Length; rank++)
        {
            var r = order[rank];
            r.Shown += ((r.Target ?? 0) - r.Shown) * 0.15;
            r.Y += (rank * RowHeight - r.Y) * 0.2;
        }
        InvalidateVisual();
    }

    /// <summary>Highest value first (the leader). Ties keep their order.</summary>
    Racer[] Order() => _racers.OrderByDescending(r => Math.Round(r.Shown, 1)).ToArray();

    public override void Render(DrawingContext ctx)
    {
        double barLeft = LabelWidth, barWidth = Bounds.Width - LabelWidth - 4;
        if (barWidth <= 20)
            return;
        var leader = Order()[0];
        bool hasLeader = leader.Target is >= 60;
        foreach (var r in _racers)
        {
            double y = r.Y + (RowHeight - BarHeight) / 2;
            bool isLeader = hasLeader && r == leader;

            Text(ctx, r.Name(), isLeader ? TextBrush : MutedBrush, 14, isLeader, new Point(0, y + 4));
            ctx.DrawRectangle(TrackBrush, null, new Rect(barLeft, y, barWidth, BarHeight), 13, 13);
            double fill = r.Target is null ? 0 : Math.Max(BarHeight, barWidth * Math.Clamp(r.Shown, 0, 100) / 100);
            if (r.Target is not null)
                ctx.DrawRectangle(r.Brush, isLeader ? new Pen(LeaderBrush, 2) : null, new Rect(barLeft, y, fill, BarHeight), 13, 13);

            var value = new FormattedText(r.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 13, Brush.Parse("#151B27"));
            value.SetFontWeight(FontWeight.Bold);
            // Inside the bar's end when it fits, otherwise right after it.
            bool inside = r.Target is not null && fill > value.Width + 18;
            var at = inside ? new Point(barLeft + fill - value.Width - 10, y + 5) : new Point(barLeft + fill + 8, y + 5);
            ctx.DrawText(inside ? value : new FormattedText(r.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 13, MutedBrush), at);

            if (isLeader)
            {
                string tag = "◀ " + L.T("Mest sandsynlige synder", "Most likely culprit", "Mest sannolika boven", "Mest sannsynlige synder", "Wahrscheinlichster Übeltäter");
                double after = barLeft + fill + (inside ? 10 : value.Width + 20);
                Text(ctx, tag, LeaderBrush, 13, true, new Point(Math.Min(after, Math.Max(barLeft, Bounds.Width - 220)), y + 5));
            }
        }
    }

    static void Text(DrawingContext ctx, string text, IBrush brush, double size, bool bold, Point at)
    {
        var t = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        if (bold)
            t.SetFontWeight(FontWeight.Bold);
        ctx.DrawText(t, at);
    }
}
