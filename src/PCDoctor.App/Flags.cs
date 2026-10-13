using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using PCDoctor.Core;

namespace PCDoctor.App;

/// <summary>
/// Small drawn flags for the language picker. Drawn with shapes because Windows
/// does not show flag emoji (it shows the letters "DK" instead).
/// </summary>
static class Flags
{
    const double W = 27, H = 18;

    public static Control Create(AnswerLanguage language)
    {
        var canvas = new Canvas { Width = W, Height = H, ClipToBounds = true };
        switch (language)
        {
            case AnswerLanguage.Danish:
                NordicCross(canvas, "#C8102E", "#FFFFFF", null);
                break;
            case AnswerLanguage.Swedish:
                NordicCross(canvas, "#006AA7", "#FECC00", null);
                break;
            case AnswerLanguage.Norwegian:
                NordicCross(canvas, "#BA0C2F", "#FFFFFF", "#00205B");
                break;
            case AnswerLanguage.German:
                Rect(canvas, 0, 0, W, H / 3, "#000000");
                Rect(canvas, 0, H / 3, W, H / 3, "#DD0000");
                Rect(canvas, 0, 2 * H / 3, W, H / 3, "#FFCE00");
                break;
            case AnswerLanguage.English:
                Rect(canvas, 0, 0, W, H, "#012169");
                Line(canvas, 0, 0, W, H, "#FFFFFF", 3.6);
                Line(canvas, W, 0, 0, H, "#FFFFFF", 3.6);
                Line(canvas, 0, 0, W, H, "#C8102E", 1.2);
                Line(canvas, W, 0, 0, H, "#C8102E", 1.2);
                Rect(canvas, W / 2 - 3, 0, 6, H, "#FFFFFF");
                Rect(canvas, 0, H / 2 - 3, W, 6, "#FFFFFF");
                Rect(canvas, W / 2 - 1.8, 0, 3.6, H, "#C8102E");
                Rect(canvas, 0, H / 2 - 1.8, W, 3.6, "#C8102E");
                break;
        }
        return new Border { Child = canvas, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) };
    }

    /// <summary>Background, cross and an optional inner cross (Norway), with the vertical bar left of centre.</summary>
    static void NordicCross(Canvas c, string background, string cross, string? inner)
    {
        Rect(c, 0, 0, W, H, background);
        double x = 8, y = H / 2, t = inner is null ? 3.6 : 4.4;
        Rect(c, x, 0, t, H, cross);
        Rect(c, 0, y - t / 2, W, t, cross);
        if (inner is not null)
        {
            Rect(c, x + 1.1, 0, t - 2.2, H, inner);
            Rect(c, 0, y - t / 2 + 1.1, W, t - 2.2, inner);
        }
    }

    static void Rect(Canvas c, double x, double y, double w, double h, string color)
    {
        var r = new Rectangle { Width = w, Height = h, Fill = Brush.Parse(color) };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        c.Children.Add(r);
    }

    static void Line(Canvas c, double x1, double y1, double x2, double y2, string color, double thickness) =>
        c.Children.Add(new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = Brush.Parse(color), StrokeThickness = thickness });
}
