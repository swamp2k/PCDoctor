using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace PCDoctor.App;

/// <summary>Small drawn flags for the language picker (Windows does not render flag emoji).</summary>
static class Flags
{
    public static readonly (string Code, string Name)[] Languages =
    [
        ("da", "Dansk"),
        ("en", "English"),
        ("no", "Norsk"),
        ("sv", "Svenska"),
    ];

    const double W = 24, H = 16;

    public static Control Item(string code, string name) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children =
        {
            new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5), Child = Draw(code), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    static Canvas Draw(string code)
    {
        var c = new Canvas { Width = W, Height = H, ClipToBounds = true };
        switch (code)
        {
            case "da":
                AddRect(c, "#C8102E", 0, 0, W, H);
                NordicCross(c, "#FFFFFF", 2);
                break;
            case "no":
                AddRect(c, "#BA0C2F", 0, 0, W, H);
                NordicCross(c, "#FFFFFF", 4);
                NordicCross(c, "#00205B", 2);
                break;
            case "sv":
                AddRect(c, "#006AA7", 0, 0, W, H);
                NordicCross(c, "#FECC00", 2);
                break;
            default: // English: simplified Union Jack
                AddRect(c, "#012169", 0, 0, W, H);
                AddLine(c, "#FFFFFF", 3, 0, 0, W, H);
                AddLine(c, "#FFFFFF", 3, W, 0, 0, H);
                AddLine(c, "#C8102E", 1, 0, 0, W, H);
                AddLine(c, "#C8102E", 1, W, 0, 0, H);
                AddRect(c, "#FFFFFF", W / 2 - 2.5, 0, 5, H);
                AddRect(c, "#FFFFFF", 0, H / 2 - 2.5, W, 5);
                AddRect(c, "#C8102E", W / 2 - 1.5, 0, 3, H);
                AddRect(c, "#C8102E", 0, H / 2 - 1.5, W, 3);
                break;
        }
        return c;
    }

    // Nordic cross: the upright sits left of centre.
    static void NordicCross(Canvas c, string color, double thickness)
    {
        double x = W * 3 / 8, y = H / 2;
        AddRect(c, color, x - thickness, 0, thickness * 2, H);
        AddRect(c, color, 0, y - thickness, W, thickness * 2);
    }

    static void AddRect(Canvas c, string color, double x, double y, double w, double h)
    {
        var r = new Rectangle { Width = w, Height = h, Fill = Brush.Parse(color) };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        c.Children.Add(r);
    }

    static void AddLine(Canvas c, string color, double thickness, double x1, double y1, double x2, double y2) =>
        c.Children.Add(new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = Brush.Parse(color), StrokeThickness = thickness });
}
