using System.ComponentModel;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using PCDoctor.Core;

namespace PCDoctor.App;

/// <summary>
/// A text in all five languages that switches by itself when the flag changes.
/// In XAML: <c>Text="{app:Tr Samtale, Chat, Samtal, Samtale, Chat}"</c> (Danish, English, Swedish, Norwegian, German).
/// Put a text in 'single quotes' when it contains a comma.
/// In code: <c>Tr.Bind(button, Button.ContentProperty, "Opdater", "Refresh", …)</c>.
/// </summary>
public sealed class Tr : MarkupExtension
{
    public Tr() { }

    public Tr(string da, string en, string sv, string no, string de) => (Da, En, Sv, No, De) = (da, en, sv, no, de);

    public string Da { get; set; } = "";
    public string En { get; set; } = "";
    public string Sv { get; set; } = "";
    public string No { get; set; } = "";
    public string De { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Binding(Da, En, Sv, No, De);

    /// <summary>Binds a property of a control made in code to a text in all five languages.</summary>
    public static void Bind(AvaloniaObject target, AvaloniaProperty property, string da, string en, string sv, string no, string de) =>
        target.Bind(property, Binding(da, en, sv, no, de));

    static ReflectionBinding Binding(string da, string en, string sv, string no, string de) =>
        new(nameof(Language.Current))
        {
            Source = Language.Instance,
            Mode = BindingMode.OneWay,
            Converter = new FuncValueConverter<AnswerLanguage, string>(language => L.Pick(language, da, en, sv, no, de)),
        };

    /// <summary>Tells bindings when the language changes. One shared object, so windows that close are not kept alive.</summary>
    public sealed class Language : INotifyPropertyChanged
    {
        public static readonly Language Instance = new();

        Language() => L.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));

        public AnswerLanguage Current => L.Current;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

/// <summary>Texts used on more than one page.</summary>
static class CommonText
{
    public static string Investigating => L.T("Undersøger…", "Investigating…", "Undersöker…", "Undersøker…", "Untersuche…");

    public static string AskPcDoctor => L.T("Spørg PC Doctor", "Ask PC Doctor", "Fråga PC Doctor", "Spør PC Doctor", "PC Doctor fragen");

    public static string QuestionSent => L.T("Spørgsmålet er sendt til PC Doctor.", "The question was sent to PC Doctor.",
        "Frågan har skickats till PC Doctor.", "Spørsmålet er sendt til PC Doctor.", "Die Frage wurde an PC Doctor gesendet.");

    public static string Busy => L.T("PC Doctor er optaget. Vent til den er færdig, og prøv igen.", "PC Doctor is busy. Wait until it is done and try again.",
        "PC Doctor är upptagen. Vänta tills den är klar och försök igen.", "PC Doctor er opptatt. Vent til den er ferdig, og prøv igjen.",
        "PC Doctor ist beschäftigt. Warte, bis er fertig ist, und versuche es erneut.");

    public static string Refresh => L.T("Opdater", "Refresh", "Uppdatera", "Oppdater", "Aktualisieren");

    public static void BindRefresh(Avalonia.AvaloniaObject target, Avalonia.AvaloniaProperty property) =>
        Tr.Bind(target, property, "Opdater", "Refresh", "Uppdatera", "Oppdater", "Aktualisieren");
}
