using System.Globalization;

namespace PCDoctor.Core;

/// <summary>
/// The app's own words (menus, buttons, reports) in the language picked with the flag. Danish by default.
/// Every text is written in all five languages right where it is used, always in this order:
/// <c>L.T("dansk", "English", "svenska", "norsk", "Deutsch")</c>.
/// </summary>
public static class L
{
    static AnswerLanguage _current = AnswerLanguage.Danish;

    /// <summary>The language picked with the flag. Setting it raises <see cref="Changed"/>.</summary>
    public static AnswerLanguage Current
    {
        get => _current;
        set
        {
            if (_current == value) return;
            _current = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Raised when the language changes, so pages can redraw their text.</summary>
    public static event Action? Changed;

    /// <summary>The text in the current language.</summary>
    public static string T(string da, string en, string sv, string no, string de) => Pick(_current, da, en, sv, no, de);

    /// <summary>The text in a given language.</summary>
    public static string Pick(AnswerLanguage language, string da, string en, string sv, string no, string de) => language switch
    {
        AnswerLanguage.English => en,
        AnswerLanguage.Swedish => sv,
        AnswerLanguage.Norwegian => no,
        AnswerLanguage.German => de,
        _ => da,
    };

    /// <summary>How numbers are written in the current language (decimal comma or point).</summary>
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(_current switch
    {
        AnswerLanguage.English => "en-GB",
        AnswerLanguage.Swedish => "sv-SE",
        AnswerLanguage.Norwegian => "nb-NO",
        AnswerLanguage.German => "de-DE",
        _ => "da-DK",
    });
}
