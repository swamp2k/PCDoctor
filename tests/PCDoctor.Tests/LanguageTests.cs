using System.Text.RegularExpressions;
using PCDoctor.Core;
using PCDoctor.Diagnostics;

namespace PCDoctor.Tests;

/// <summary>Changing the language is app-wide, so these tests never run at the same time as the others.</summary>
[CollectionDefinition(nameof(LanguageCollection), DisableParallelization = true)]
public class LanguageCollection;

[Collection(nameof(LanguageCollection))]
public class LanguageTests
{
    [Theory]
    [InlineData(AnswerLanguage.Danish, "da")]
    [InlineData(AnswerLanguage.English, "en")]
    [InlineData(AnswerLanguage.Swedish, "sv")]
    [InlineData(AnswerLanguage.Norwegian, "no")]
    [InlineData(AnswerLanguage.German, "de")]
    public void Each_language_picks_its_own_text(AnswerLanguage language, string expected) =>
        Assert.Equal(expected, L.Pick(language, "da", "en", "sv", "no", "de"));

    [Fact]
    public void Reports_follow_the_language_and_tell_when_it_changes()
    {
        var facts = new PlainReport.NetworkFacts(true, true, true, 0, 15, true, true);
        int changes = 0;
        void Count() => changes++;
        L.Changed += Count;
        try
        {
            Assert.Equal("Internettet virker. Svartid 15 ms.", PlainReport.NetworkVerdict(facts));
            L.Current = AnswerLanguage.English;
            Assert.Equal("The internet works. Response time 15 ms.", PlainReport.NetworkVerdict(facts));
            L.Current = AnswerLanguage.German;
            Assert.Equal("Das Internet funktioniert. Antwortzeit 15 ms.", PlainReport.NetworkVerdict(facts));
            Assert.Equal(2, changes);
        }
        finally
        {
            L.Current = AnswerLanguage.Danish;
            L.Changed -= Count;
        }
    }

    /// <summary>
    /// Every text in the windows must go through {app:Tr …} so it switches language.
    /// Only names that are the same in every language (CPU, RAM, the mode names) may be written plainly.
    /// </summary>
    [Fact]
    public void No_fixed_text_left_in_the_windows()
    {
        string app = Path.Combine(RepoRoot(), "src", "PCDoctor.App");
        string[] sameInAllLanguages = ["PC Doctor", "CPU", "RAM", "Disk", "FPS", "Read-only", "Assisted", "YOLO", "sk-ant-…"];
        var attribute = new Regex("""\b(Text|Content|Header|Title|ToolTip\.Tip|PlaceholderText|Label)="([^"{][^"]*)" """.Trim());
        var leftovers = Directory.GetFiles(app, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .SelectMany(l => attribute.Matches(l.Text).Select(m => (l.File, l.Line, Value: m.Groups[2].Value)))
            .Where(x => !sameInAllLanguages.Contains(x.Value) && x.Value.Any(char.IsLetter))
            .Select(x => $"{x.File}:{x.Line} \"{x.Value}\"")
            .ToList();
        Assert.Empty(leftovers);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCDoctor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("PCDoctor.sln not found");
    }
}
