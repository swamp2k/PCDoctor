namespace PCDoctor.Core;

/// <summary>The language the agent answers in. Picked with the flag in the top bar; Danish by default.</summary>
public enum AnswerLanguage
{
    Danish,
    English,
    Swedish,
    Norwegian,
    German,
}

public static class AnswerLanguages
{
    /// <summary>How the language is named to the model. Explicit, because Danish and Norwegian are easily mixed up.</summary>
    public static string PromptName(this AnswerLanguage language) => language switch
    {
        AnswerLanguage.Danish => "Danish (dansk, as written in Denmark; not Norwegian or Swedish)",
        AnswerLanguage.English => "English",
        AnswerLanguage.Swedish => "Swedish (svenska)",
        AnswerLanguage.Norwegian => "Norwegian Bokmål (norsk)",
        AnswerLanguage.German => "German (Deutsch)",
        _ => "Danish",
    };

    /// <summary>The language's own name, shown in the picker.</summary>
    public static string NativeName(this AnswerLanguage language) => language switch
    {
        AnswerLanguage.Danish => "Dansk",
        AnswerLanguage.English => "English",
        AnswerLanguage.Swedish => "Svenska",
        AnswerLanguage.Norwegian => "Norsk",
        AnswerLanguage.German => "Deutsch",
        _ => language.ToString(),
    };
}
