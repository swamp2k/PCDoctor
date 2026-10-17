using System.Text;
using System.Text.Json;
using PCDoctor.Agent;
using PCDoctor.Core;
using PCDoctor.Diagnostics;

namespace PCDoctor.Tests;

public class LogReviewTests
{
    static string TempFile(string name, IEnumerable<string> lines)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pcd-lr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Recognises_games_from_the_first_lines()
    {
        Assert.Equal("minecraft", LogReview.Recognise(["[12:00:01] [main/INFO]: Loading Minecraft 1.20.1"], "latest.log")?.Key);
        Assert.Equal("roblox", LogReview.Recognise(["2024-01-01T12:00:00Z,1.0,abc,6 [FLog::Output] Roblox version"], "x.log")?.Key);
        Assert.Equal("unity", LogReview.Recognise(["Initialize engine version: 2021.3.4f1"], "Player.log")?.Key);
        var fortnite = LogReview.Recognise(["Log file open, 01/02/24", "LogInit: Build: ++Fortnite+Release"], "FortniteGame.log");
        Assert.Equal(("unreal", "Fortnite"), (fortnite?.Key, fortnite?.Name));
        Assert.Equal("Rocket League", LogReview.Recognise(["LogInit: Unreal Engine 3 TAGame"], "x.log")?.Name);
    }

    [Fact]
    public void Unknown_log_is_not_guessed()
    {
        Assert.Null(LogReview.Recognise(["hello", "this is some random application log"], "app.log"));
    }

    [Fact]
    public void Only_the_first_50_lines_count()
    {
        var lines = Enumerable.Repeat("nothing here", 60).Append("[main/INFO] Loading Minecraft");
        Assert.Null(LogReview.Recognise(lines, "x.log"));
    }

    [Fact]
    public void Smart_keeps_problem_lines_with_context_and_drops_the_rest()
    {
        var lines = new List<string>();
        for (int i = 0; i < 5000; i++) lines.Add($"[12:00:{i % 60:00}] [main/INFO]: all fine {i}");
        lines.Insert(2500, "[12:10:00] [main/ERROR]: Failed to load mod coolmod");
        lines.Insert(2501, "java.lang.NullPointerException: x");
        lines.Insert(2502, "\tat net.coolmod.Main.init(Main.java:12)");
        string path = TempFile("latest.log", lines);
        var r = LogReview.Smart(path, "minecraft");
        Assert.Contains("Failed to load mod coolmod", r.Text);
        Assert.Contains("NullPointerException", r.Text);
        Assert.Contains("Main.java:12", r.Text);
        Assert.DoesNotContain("all fine 100", r.Text);
        Assert.True(r.Text.Length < 2000);
        Assert.Equal(5003, r.TotalLines);
    }

    [Fact]
    public void Repeated_lines_are_counted_not_repeated()
    {
        string path = TempFile("Player.log", Enumerable.Range(0, 1000).Select(i => $"ERROR: texture {i} missing"));
        var r = LogReview.Smart(path, "unity");
        Assert.Equal(1000, r.ProblemLines);
        Assert.Equal(3, r.Text.Split("ERROR: texture").Length - 1 - (r.Text.Contains("more of \"ERROR: texture") ? 1 : 0));
        Assert.Contains("997 more", r.Text);
    }

    [Fact]
    public void Without_problems_the_end_of_the_log_is_sent()
    {
        string path = TempFile("a.log", Enumerable.Range(0, 500).Select(i => $"line {i} ok"));
        var r = LogReview.Smart(path, "other");
        Assert.Equal(0, r.ProblemLines);
        Assert.Contains("line 499 ok", r.Text);
        Assert.DoesNotContain("line 10 ok", r.Text);
    }

    [Fact]
    public void Huge_output_is_capped()
    {
        string path = TempFile("big.log", Enumerable.Range(0, 20000).Select(i => $"Error: unique problem {Guid.NewGuid()} number {i}"));
        var r = LogReview.Smart(path, "unreal");
        Assert.True(r.Cut);
        Assert.True(r.Text.Length < LogReview.SmartLimit + 5000);
    }

    [Fact]
    public void Whole_sends_start_and_end_of_a_big_file()
    {
        string path = TempFile("big.log", Enumerable.Range(0, 100000).Select(i => $"line {i} some filler text to make it long enough"));
        var r = LogReview.Whole(path);
        Assert.True(r.Cut);
        Assert.Contains("line 0 ", r.Text);
        Assert.Contains("line 99999 ", r.Text);
        Assert.True(r.Text.Length <= LogReview.WholeLimit + 200);
    }

    [Fact]
    public async Task Tool_reads_only_the_picked_file_and_needs_no_arguments()
    {
        var selection = new LogReviewSelection();
        var tool = new ReviewLogTool(selection);
        Assert.Equal(ToolKind.ReadOnly, tool.Kind);
        Assert.True((await tool.RunAsync(JsonDocument.Parse("{\"path\":\"C:\\\\secret.txt\"}").RootElement, default)).IsError);

        selection.Path = TempFile("latest.log", ["[main/ERROR]: boom"]);
        selection.GameKey = "minecraft";
        selection.GameName = "Minecraft";
        var r = JsonDocument.Parse((await tool.RunAsync(JsonDocument.Parse("{}").RootElement, default)).Content).RootElement;
        Assert.Contains("boom", r.GetProperty("excerpt").GetString());
        Assert.Equal("latest.log", r.GetProperty("file").GetString());
    }

    [Fact]
    public void System_prompt_names_the_tool()
    {
        Assert.Contains("review_log", SystemPrompt.For(AnswerLanguage.Danish));
    }
}
