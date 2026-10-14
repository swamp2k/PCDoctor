using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PCDoctor.Core;

namespace PCDoctor.Diagnostics;

/// <summary>A game's published PC requirements as lines of text, e.g. "Memory: 8 GB RAM".</summary>
public sealed record GameRequirements(string Game, string Source, string? Checked, List<string> Minimum, List<string> Recommended, string? Note = null);

/// <summary>What this PC has, for the checks we can do without knowing graphics card models.</summary>
public sealed record PcFacts(double RamGb, double LargestFreeDiskGb, string? LargestFreeDrive, int LogicalCpus, bool Is64Bit, string Os)
{
    public static PcFacts Current()
    {
        var drives = DriveInfo.GetDrives().Where(d => { try { return d.IsReady && d.DriveType == DriveType.Fixed; } catch (IOException) { return false; } }).ToList();
        var best = drives.MaxBy(d => d.AvailableFreeSpace);
        return new PcFacts(
            Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0, 1),
            best is null ? 0 : Math.Round(best.AvailableFreeSpace / 1073741824.0, 1),
            best?.Name,
            Environment.ProcessorCount,
            Environment.Is64BitOperatingSystem,
            Environment.OSVersion.VersionString);
    }
}

/// <summary>
/// game_requirements: "Can my PC run this game?" Gives the game's minimum and recommended requirements
/// (built in for Minecraft, Fortnite and Roblox, which are not on Steam; other games from the Steam store)
/// and compares the parts that can be compared as numbers: RAM and free disk space.
/// </summary>
public sealed partial class GameRequirementsTool : ITool
{
    /// <summary>
    /// Copied from the publishers' own pages on 2026-10-14. Requirements change; the source link is always given.
    /// </summary>
    public static readonly GameRequirements[] BuiltIn =
    [
        new("Minecraft: Java Edition", "https://www.minecraft.net/en-us/article/minecraft-java-edition-system-requirements", "2026-10-14",
            ["64-bit system", "Memory: 8 GB RAM with a separate graphics card, or 12 GB RAM with built-in (integrated) graphics", "Processor: 4 cores",
             "Graphics: Vulkan 1.3 capable, at least 2 GB VRAM", "(1080p at 30 FPS on Fast graphics)"],
            ["Memory: 16 GB RAM", "Processor: a stronger modern processor", "Graphics: 6 GB VRAM", "(1080p at 60 FPS on Fancy graphics)"],
            "Raised by Mojang on 21 July 2026. Mods and shaders need more than this. Minecraft Bedrock (Microsoft Store) needs less."),
        new("Fortnite", "https://www.epicgames.com/help/en-US/c-202300000001636/a202300000012731", "2026-10-14",
            ["OS: Windows 10 version 22H2 (64-bit) or Windows 10 Enterprise 21H2 (64-bit)", "Processor: Core i3-3225 3.3 GHz",
             "Graphics: Intel HD 4000 or AMD Radeon Vega 8", "Memory: 8 GB RAM"],
            ["OS: Windows 10/11 64-bit", "Processor: Core i5-7300U 3.5 GHz, AMD Ryzen 3 3300U, or equivalent",
             "Graphics: Nvidia GTX 960, AMD R9 280, or equivalent DX11 GPU, 2 GB VRAM", "Memory: 16 GB RAM or higher", "Storage: NVMe solid state drive (SSD)"]),
        new("Roblox", "https://en.help.roblox.com/hc/articles/203312800", "2026-10-14",
            ["OS: 64-bit Windows 10 or Windows 11", "Processor: from 2005 or newer, 1.6 GHz or faster", "Graphics: DirectX 10 feature level or higher",
             "Memory: 1 GB RAM", "Storage: 300 MB free", "Internet: 4-8 Mb/s"],
            []),
    ];

    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["minecraft"] = "Minecraft: Java Edition", ["minecraft java"] = "Minecraft: Java Edition", ["minecraft java edition"] = "Minecraft: Java Edition",
        ["fortnite"] = "Fortnite", ["roblox"] = "Roblox",
    };

    readonly HttpClient _http;
    readonly Func<PcFacts> _pc;

    /// <param name="http">For tests. Defaults to a client with a 15-second timeout.</param>
    /// <param name="pc">For tests. Defaults to this PC.</param>
    public GameRequirementsTool(HttpClient? http = null, Func<PcFacts>? pc = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _pc = pc ?? PcFacts.Current;
    }

    public string Name => "game_requirements";
    public string Description =>
        "Can this PC run a game? Returns the game's minimum and recommended PC requirements (built in for Minecraft, Fortnite and Roblox; " +
        "any other game is looked up in the Steam store) and checks RAM and free disk space against them. " +
        "Processor and graphics card cannot be compared automatically: call system_hardware and compare those yourself, " +
        "and say how sure you are.";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["game"] = new JsonObject { ["type"] = "string", ["description"] = "The game's name, e.g. \"Fortnite\" or \"Cyberpunk 2077\"." },
        },
        ["required"] = new JsonArray("game"),
    };

    public async Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct)
    {
        string? game = input.TryGetProperty("game", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(game) || game.Length > 100)
            return ToolResult.Error("Give the game's name.");

        GameRequirements? req = Aliases.TryGetValue(game, out var known) ? BuiltIn.First(b => b.Game == known) : null;
        string? lookupError = null;
        List<string> otherMatches = [];
        if (req is null)
            (req, otherMatches, lookupError) = await FromSteam(game, ct);
        if (req is null)
            return ToolResult.Json(new
            {
                game,
                found = false,
                error = lookupError,
                note = "No requirements found. The game may not be on Steam (check the publisher's website), or the name may be spelled differently.",
            });

        var pc = _pc();
        return ToolResult.Json(new
        {
            game = req.Game,
            found = true,
            source = req.Source,
            copied_on = req.Checked,
            minimum = req.Minimum,
            recommended = req.Recommended,
            game_note = req.Note,
            other_matches = otherMatches.Count == 0 ? null : otherMatches,
            this_pc = new { ram_gb = pc.RamGb, largest_free_disk_gb = pc.LargestFreeDiskGb, drive = pc.LargestFreeDrive, logical_cpus = pc.LogicalCpus, is_64_bit = pc.Is64Bit, os = pc.Os },
            checks = Compare(req, pc),
            note = "checks only covers what can be compared as numbers. Compare processor and graphics card with system_hardware yourself. " +
                   "Meeting the minimum means the game starts on low settings; recommended means it plays well on medium.",
        });
    }

    /// <summary>RAM and storage from the requirement lines, compared with this PC.</summary>
    public static List<object> Compare(GameRequirements req, PcFacts pc)
    {
        var checks = new List<object>();
        double? minRam = Ram(req.Minimum), recRam = Ram(req.Recommended);
        if (minRam is not null || recRam is not null)
        {
            string verdict = pc.RamGb + 0.5 >= (recRam ?? double.MaxValue) ? "meets recommended"
                : pc.RamGb + 0.5 >= (minRam ?? 0) ? (recRam is null ? "meets minimum" : "meets minimum, below recommended")
                : "below minimum";
            checks.Add(new { part = "RAM", this_pc_gb = pc.RamGb, minimum_gb = minRam, recommended_gb = recRam, verdict });
        }
        double? storage = Storage(req.Recommended) ?? Storage(req.Minimum);
        if (storage is not null)
            checks.Add(new { part = "Free disk space", this_pc_gb = pc.LargestFreeDiskGb, drive = pc.LargestFreeDrive, needed_gb = storage,
                verdict = pc.LargestFreeDiskGb >= storage ? "enough space" : "not enough free space" });
        if (req.Minimum.Concat(req.Recommended).Any(l => l.Contains("64-bit", StringComparison.OrdinalIgnoreCase)))
            checks.Add(new { part = "64-bit Windows", this_pc = pc.Is64Bit, verdict = pc.Is64Bit ? "ok" : "this game needs 64-bit Windows" });
        return checks;
    }

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(GB|MB)", RegexOptions.IgnoreCase)]
    private static partial Regex Size();

    static double? Ram(List<string> lines)
    {
        string? line = lines.FirstOrDefault(l => l.StartsWith("Memory", StringComparison.OrdinalIgnoreCase));
        return line is null ? null : FirstSize(line);
    }

    static double? Storage(List<string> lines)
    {
        string? line = lines.FirstOrDefault(l => Regex.IsMatch(l, @"^(Storage|Hard Drive|Hard Disk|Disk Space)", RegexOptions.IgnoreCase));
        return line is null ? null : FirstSize(line);
    }

    static double? FirstSize(string line)
    {
        var m = Size().Match(line);
        if (!m.Success) return null;
        double v = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        return m.Groups[2].Value.Equals("MB", StringComparison.OrdinalIgnoreCase) ? Math.Round(v / 1024, 2) : v;
    }

    async Task<(GameRequirements?, List<string>, string?)> FromSteam(string game, CancellationToken ct)
    {
        try
        {
            string search = await _http.GetStringAsync($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(game)}&l=english&cc=DK", ct);
            using var s = JsonDocument.Parse(search);
            var items = s.RootElement.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array
                ? it.EnumerateArray().Where(i => i.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number).ToList()
                : [];
            if (items.Count == 0)
                return (null, [], $"Steam has no game matching '{game}'.");

            // Prefer an exact name match; otherwise Steam's first (most relevant) result.
            var pick = items.FirstOrDefault(i => string.Equals(i.GetProperty("name").GetString(), game, StringComparison.OrdinalIgnoreCase));
            if (pick.ValueKind == JsonValueKind.Undefined) pick = items[0];
            int appId = pick.GetProperty("id").GetInt32();
            var others = items.Where(i => i.GetProperty("id").GetInt32() != appId).Take(4).Select(i => i.GetProperty("name").GetString() ?? "").ToList();

            string details = await _http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&l=english", ct);
            var req = ParseSteamDetails(details, appId);
            return req is null
                ? (null, others, $"Steam lists '{pick.GetProperty("name").GetString()}' but gives no PC requirements for it.")
                : (req, others, null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                                  || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, [], "The Steam store could not be reached or answered unexpectedly: " + e.Message);
        }
    }

    /// <summary>Steam's appdetails answer: pc_requirements.minimum/recommended are small HTML lists.</summary>
    public static GameRequirements? ParseSteamDetails(string json, int appId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var app)
            || !app.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;
        string name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        // pc_requirements is an object, or an empty array when the game has none.
        if (!data.TryGetProperty("pc_requirements", out var pcr) || pcr.ValueKind != JsonValueKind.Object)
            return null;
        var min = HtmlLines(pcr.TryGetProperty("minimum", out var a) ? a.GetString() : null);
        var rec = HtmlLines(pcr.TryGetProperty("recommended", out var b) ? b.GetString() : null);
        if (min.Count == 0 && rec.Count == 0)
            return null;
        return new GameRequirements(name, $"https://store.steampowered.com/app/{appId}", DateTime.Now.ToString("yyyy-MM-dd"), min, rec);
    }

    [GeneratedRegex(@"<br\s*/?>|</li>|</p>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    static List<string> HtmlLines(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];
        string text = WebUtility.HtmlDecode(Tags().Replace(Breaks().Replace(html, "\n"), ""));
        return text.Split('\n')
            .Select(l => Regex.Replace(l, @"\s+", " ").Trim())
            .Where(l => l.Length > 0 && !l.Equals("Minimum:", StringComparison.OrdinalIgnoreCase) && !l.Equals("Recommended:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Length > 250 ? l[..250] + " […]" : l)
            .Take(15)
            .ToList();
    }
}
