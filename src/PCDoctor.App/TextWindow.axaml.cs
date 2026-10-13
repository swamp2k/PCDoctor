using Avalonia.Controls;

namespace PCDoctor.App;

public partial class TextWindow : Window
{
    public TextWindow() : this("", "") { }

    public TextWindow(string title, string text)
    {
        InitializeComponent();
        Title = title;
        Body.Text = text;
    }
}
