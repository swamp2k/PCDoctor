using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using PCDoctor.Diagnostics;

namespace PCDoctor.App;

/// <summary>A status light. None is a plain fact with no verdict (grey).</summary>
public enum Light { None, Green, Amber, Red }

/// <summary>The status-light colours (from Theme/Cockpit.axaml) and the rules for picking one.</summary>
static class Status
{
    public static IBrush Brush(string key) =>
        Application.Current?.TryGetResource(key, null, out var v) == true && v is IBrush b ? b : Brushes.Gray;

    public static IBrush Of(Light light) => Brush(light switch
    {
        Light.Red => "RedBrush",
        Light.Amber => "AmberBrush",
        Light.Green => "GreenBrush",
        _ => "DimBrush",
    });

    public static IBrush SoftOf(Light light) => Brush(light switch
    {
        Light.Red => "RedSoftBrush",
        Light.Amber => "AmberSoftBrush",
        Light.Green => "GreenSoftBrush",
        _ => "CardBrush",
    });

    /// <summary>A line of a diagnostics report: red if marked as a problem, yellow if marked as worth a look.</summary>
    public static Light OfLine(string line) =>
        line.Contains(PlainReport.Problem) ? Light.Red : line.Contains(PlainReport.Notice) ? Light.Amber : Light.None;

    /// <summary>The line without its marker sign, for display next to a light.</summary>
    public static string WithoutMarker(string line) =>
        System.Text.RegularExpressions.Regex.Replace(line.Replace(PlainReport.Problem, "").Replace(PlainReport.Notice, ""), " {2,}", " ").Trim();

    /// <summary>How busy something is: green under 70 %, yellow under 90 %, red from 90 %.</summary>
    public static Light OfLoad(double? percent) =>
        percent is null ? Light.None : percent >= 90 ? Light.Red : percent >= 70 ? Light.Amber : Light.Green;

    /// <summary>
    /// A part's average over a recording, judged by how much of the time it was over 90 %:
    /// red from a quarter of the time, yellow from a tenth.
    /// </summary>
    public static Light OfBusyTime(PCDoctor.Core.MetricSummary? m) =>
        m is null ? Light.None : m.PercentOfTimeAbove90 >= 25 ? Light.Red : m.PercentOfTimeAbove90 >= 10 ? Light.Amber : Light.Green;

    /// <summary>Average frames per second: red under 30, yellow under 60, green from 60.</summary>
    public static Light OfFps(double fps) => fps < 30 ? Light.Red : fps < 60 ? Light.Amber : Light.Green;

    public static Ellipse Dot(Light light, double size = 10) =>
        new() { Width = size, Height = size, Fill = Of(light), VerticalAlignment = VerticalAlignment.Center };
}

/// <summary>A ring that fills up from the top, clockwise, like a gauge in a cockpit.</summary>
public sealed class Gauge : Control
{
    double? _value;
    IBrush _color = Brushes.Gray;

    public void Set(double? percent, IBrush color)
    {
        _value = percent;
        _color = color;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        const double thickness = 6;
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= thickness * 2)
            return;
        double r = (size - thickness) / 2;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        ctx.DrawEllipse(null, new Pen(Status.Brush("LineBrush"), thickness), c, r, r);

        double pct = Math.Clamp(_value ?? 0, 0, 100);
        if (pct <= 0)
            return;
        var pen = new Pen(_color, thickness, lineCap: PenLineCap.Round);
        if (pct >= 99.9)
        {
            ctx.DrawEllipse(null, pen, c, r, r);
            return;
        }
        double angle = 2 * Math.PI * pct / 100;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(r, r), 0, pct > 50, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }
}

/// <summary>A card with a gauge, a big number and a label underneath, e.g. "72 %" over "CPU".</summary>
public sealed class GaugeTile : Border
{
    readonly Gauge _gauge = new() { Width = 52, Height = 52 };
    readonly TextBlock _value = new() { Text = "–" };
    readonly TextBlock _label = new();

    public GaugeTile()
    {
        Classes.Add("card");
        Padding = new Thickness(12, 10);
        _value.Classes.Add("big");
        _label.Classes.Add("muted");
        _label.FontSize = 12;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _value, _label } };
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _gauge, text } };
    }

    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<GaugeTile, string>(nameof(Label), "");

    /// <summary>The name under the number, e.g. "CPU". A property, so it can follow the language.</summary>
    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty)
            _label.Text = Label;
    }

    /// <summary>Shows a percentage, coloured by how busy it is unless a light is given.</summary>
    public void Show(double? percent, Light? light = null)
    {
        _value.Text = percent is null ? "–" : $"{percent:0} %";
        _gauge.Set(percent, Status.Of(light ?? Status.OfLoad(percent)));
    }
}

/// <summary>A small line icon (drawn on a 24×24 grid) in the colour of the text around it.</summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = Avalonia.Controls.Documents.TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon() => AffectsRender<Icon>(DataProperty, ForegroundProperty);

    public Icon()
    {
        Width = 18;
        Height = 18;
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        if (Data is null)
            return;
        double scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using (ctx.PushTransform(Matrix.CreateScale(scale, scale)))
            ctx.DrawGeometry(null, new Pen(Foreground ?? Brushes.Gray, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Data);
    }
}
