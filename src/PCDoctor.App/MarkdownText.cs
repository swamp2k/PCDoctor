using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PCDoctor.App;

/// <summary>
/// Shows the simple Markdown PC Doctor writes: "## " headings, "- " and "1. " lists, **bold**, *italic* and `code`.
/// Anything else is shown as plain text, so nothing is ever lost.
/// </summary>
public sealed partial class MarkdownText : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownText, string?>(nameof(Markdown));

    static MarkdownText() => MarkdownProperty.Changed.AddClassHandler<MarkdownText>((m, _) => m.Build());

    public MarkdownText() => Spacing = 4;

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    void Build()
    {
        Children.Clear();
        foreach (string raw in (Markdown ?? "").Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
            {
                Children.Add(new Border { Height = 4 });
                continue;
            }
            if (Heading().Match(line) is { Success: true } h)
            {
                Children.Add(Block(h.Groups[1].Value, 15, FontWeight.Bold, new Thickness(0, 6, 0, 0)));
                continue;
            }
            if (Bullet().Match(line) is { Success: true } b)
            {
                Children.Add(Item(b.Groups[1].Value.Length, "•", b.Groups[2].Value));
                continue;
            }
            if (Numbered().Match(line) is { Success: true } n)
            {
                Children.Add(Item(n.Groups[1].Value.Length, n.Groups[2].Value + ".", n.Groups[3].Value));
                continue;
            }
            Children.Add(Block(line, 14, FontWeight.Normal, default));
        }
    }

    static Control Item(int indent, string mark, string text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*"), Margin = new Thickness(Math.Min(indent, 8) * 6, 0, 0, 0) };
        grid.Children.Add(new TextBlock { Text = mark, FontSize = 14, LineHeight = 21 });
        var body = Block(text, 14, FontWeight.Normal, default);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    static SelectableTextBlock Block(string text, double size, FontWeight weight, Thickness margin)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight, LineHeight = size * 1.5, Margin = margin };
        block.Inlines = Inline(text);
        return block;
    }

    /// <summary>**bold**, *italic* and `code` inside one line.</summary>
    static InlineCollection Inline(string text)
    {
        var inlines = new InlineCollection();
        int at = 0;
        foreach (Match m in Span().Matches(text))
        {
            if (m.Index > at)
                inlines.Add(new Run(text[at..m.Index]));
            if (m.Groups["b"].Success)
                inlines.Add(new Run(m.Groups["b"].Value) { FontWeight = FontWeight.Bold });
            else if (m.Groups["i"].Success)
                inlines.Add(new Run(m.Groups["i"].Value) { FontStyle = FontStyle.Italic });
            else
                inlines.Add(new Run(m.Groups["c"].Value) { FontFamily = new FontFamily("Consolas,Menlo,monospace") });
            at = m.Index + m.Length;
        }
        if (at < text.Length)
            inlines.Add(new Run(text[at..]));
        return inlines;
    }

    [GeneratedRegex(@"^#{1,4}\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(\s*)(\d+)[.)]\s+(.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"\*\*(?<b>[^*]+)\*\*|(?<![*\w])\*(?<i>[^*\s][^*]*)\*(?![*\w])|`(?<c>[^`]+)`")]
    private static partial Regex Span();
}
