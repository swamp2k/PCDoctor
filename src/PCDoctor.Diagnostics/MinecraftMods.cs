using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCDoctor.Diagnostics;

/// <summary>One mod file in a Minecraft mods folder, read from the metadata inside the jar.</summary>
/// <param name="Loader">"fabric", "quilt", "forge", "neoforge", or "unknown" when the jar has no metadata we know.</param>
/// <param name="Requires">Mod ids this mod says it cannot run without (Minecraft, Java and the loader itself left out).</param>
/// <param name="Provides">Mod ids this jar supplies: its own id, aliases and mods bundled inside it.</param>
public sealed record ModInfo(
    string File,
    string Id,
    string Name,
    string? Version,
    string Loader,
    List<string> Requires,
    List<string> Provides,
    bool Disabled,
    string? Error);

/// <summary>Something likely wrong with a mods folder, in plain words for the model.</summary>
/// <param name="Kind">"missing_dependency", "duplicate", "wrong_loader" or "unreadable".</param>
public sealed record ModProblem(string Kind, string Mod, string Detail);

public sealed record ModReport(string Folder, string Loader, int ModCount, int DisabledCount, List<ModProblem> Problems, List<ModInfo> Mods);

/// <summary>Reads the mods folder of a Minecraft install: which mods, which loader, and missing or duplicate mods.</summary>
public static partial class MinecraftMods
{
    /// <summary>Jars nested inside a mod (bundled libraries) are only read up to this size.</summary>
    const long MaxNestedBytes = 20_000_000;

    /// <summary>Not mods: the game, Java and the loaders themselves. A mod depending on these is never "missing" one.</summary>
    static readonly HashSet<string> Platform = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "quilt_base", "forge", "neoforge", "mixinextras_neoforge",
    };

    public static ModReport Read(string gameFolder)
    {
        string dir = Path.Combine(gameFolder, "mods");
        var mods = new List<ModInfo>();
        if (Directory.Exists(dir))
        {
            foreach (string f in SafeFiles(dir, "*.jar").Concat(SafeFiles(dir, "*.jar.disabled")).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                mods.Add(ReadJar(f));
        }
        return Check(gameFolder, mods);
    }

    /// <summary>Works out the install's loader and finds problems. Public so tests can feed it made-up mods.</summary>
    public static ModReport Check(string folder, List<ModInfo> mods)
    {
        var active = mods.Where(m => !m.Disabled).ToList();
        var problems = new List<ModProblem>();

        // The loader most mods are made for is the install's loader. Quilt also runs Fabric mods.
        var loaders = active.Where(m => m.Loader != "unknown").GroupBy(m => m.Loader).OrderByDescending(g => g.Count()).ToList();
        string loader = loaders.Count == 0 ? (active.Count == 0 ? "vanilla" : "unknown") : loaders[0].Key;
        if (loader == "fabric" && active.Any(m => m.Loader == "quilt")) loader = "quilt";

        // Sinytra Connector lets Fabric mods run on (Neo)Forge; then a Fabric mod there is fine.
        bool connector = active.Any(m => m.Provides.Contains("connector", StringComparer.OrdinalIgnoreCase));
        foreach (var m in active)
        {
            if (m.Error is not null) { problems.Add(new("unreadable", m.File, m.Error)); continue; }
            bool wrong = loader switch
            {
                "fabric" => m.Loader is "forge" or "neoforge" or "quilt",
                "quilt" => m.Loader is "forge" or "neoforge",
                "forge" or "neoforge" => m.Loader is "fabric" or "quilt" && !connector,
                _ => false,
            };
            if (wrong)
                problems.Add(new("wrong_loader", m.File, $"Made for {m.Loader}, but this install runs {loader}. It will not load, or will crash the game."));
        }

        foreach (var g in active.Where(m => m.Error is null && m.Loader != "unknown").GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add(new("duplicate", g.Key, "The same mod is in the folder more than once: " + string.Join(", ", g.Select(m => $"{m.File} ({m.Version ?? "?"})")) + ". Keep only one."));

        var provided = new HashSet<string>(active.SelectMany(m => m.Provides), StringComparer.OrdinalIgnoreCase);
        bool fabricApi = provided.Contains("fabric-api") || provided.Contains("fabric");
        foreach (var m in active.Where(m => m.Error is null))
        {
            foreach (string need in m.Requires.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Platform.Contains(need) || provided.Contains(need))
                    continue;
                // Fabric API ships as one jar with dozens of modules inside (fabric-*-v1); we do not always see all of them.
                if (fabricApi && need.StartsWith("fabric", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (need.Equals("quilted_fabric_api", StringComparison.OrdinalIgnoreCase) && fabricApi)
                    continue;
                problems.Add(new("missing_dependency", m.Id, $"{m.Name} ({m.File}) needs the mod '{need}', which is not in the mods folder."));
            }
        }

        return new ModReport(folder, loader, active.Count, mods.Count - active.Count, problems, mods);
    }

    public static ModInfo ReadJar(string path)
    {
        string file = Path.GetFileName(path);
        bool disabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return ReadZip(zip, file, disabled, nested: true);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return new ModInfo(file, file, file, null, "unknown", [], [], disabled, "The file could not be read as a mod (damaged, or not a jar): " + e.Message);
        }
    }

    static ModInfo ReadZip(ZipArchive zip, string file, bool disabled, bool nested)
    {
        ModInfo? info = null;
        if (Entry(zip, "quilt.mod.json") is { } quilt) info = Quilt(quilt, file, disabled);
        else if (Entry(zip, "fabric.mod.json") is { } fabric) info = Fabric(fabric, file, disabled);
        else if (Entry(zip, "META-INF/neoforge.mods.toml") is { } neo) info = Toml(neo, "neoforge", file, disabled, zip);
        else if (Entry(zip, "META-INF/mods.toml") is { } forge) info = Toml(forge, "forge", file, disabled, zip);
        else if (Entry(zip, "mcmod.info") is { } old) info = McmodInfo(old, file, disabled);

        info ??= new ModInfo(file, Path.GetFileNameWithoutExtension(file), file, null, "unknown", [], [], disabled, null);

        // Mods often bundle their libraries as jars inside the jar (Fabric "jar-in-jar", Forge "jarjar"). Those count as present.
        if (nested)
        {
            foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                         && (e.FullName.StartsWith("META-INF/jars/") || e.FullName.StartsWith("META-INF/jarjar/")) && e.Length < MaxNestedBytes))
            {
                try
                {
                    using var ms = new MemoryStream();
                    using (var s = e.Open()) s.CopyTo(ms);
                    ms.Position = 0;
                    using var inner = new ZipArchive(ms, ZipArchiveMode.Read);
                    var sub = ReadZip(inner, e.Name, false, nested: false);
                    if (sub.Loader != "unknown")
                        info.Provides.AddRange(sub.Provides);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { /* a broken bundled library is not worth reporting */ }
            }
        }
        return info;
    }

    static string? Entry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e is null || e.Length > 1_000_000) return null;
        using var r = new StreamReader(e.Open());
        return r.ReadToEnd();
    }

    static ModInfo Fabric(string json, string file, bool disabled)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var r = doc.RootElement;
        string id = Str(r, "id") ?? Path.GetFileNameWithoutExtension(file);
        var provides = new List<string> { id };
        if (r.TryGetProperty("provides", out var p) && p.ValueKind == JsonValueKind.Array)
            provides.AddRange(p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
        var requires = r.TryGetProperty("depends", out var d) && d.ValueKind == JsonValueKind.Object
            ? d.EnumerateObject().Select(x => x.Name).ToList()
            : [];
        return new ModInfo(file, id, Str(r, "name") ?? id, Str(r, "version"), "fabric", requires, provides, disabled, null);
    }

    static ModInfo Quilt(string json, string file, bool disabled)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var q = doc.RootElement.TryGetProperty("quilt_loader", out var ql) ? ql : doc.RootElement;
        string id = Str(q, "id") ?? Path.GetFileNameWithoutExtension(file);
        string name = q.TryGetProperty("metadata", out var meta) && Str(meta, "name") is { } n ? n : id;
        var provides = new List<string> { id };
        if (q.TryGetProperty("provides", out var p) && p.ValueKind == JsonValueKind.Array)
            provides.AddRange(p.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : Str(x, "id")).OfType<string>());
        var requires = new List<string>();
        if (q.TryGetProperty("depends", out var d) && d.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in d.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String) requires.Add(x.GetString()!);
                // Optional dependencies are written as objects with "optional": true.
                else if (x.ValueKind == JsonValueKind.Object && Str(x, "id") is { } did && !(x.TryGetProperty("optional", out var o) && o.ValueKind == JsonValueKind.True))
                    requires.Add(did);
            }
        }
        return new ModInfo(file, id, name, Str(q, "version"), "quilt", requires, provides, disabled, null);
    }

    [GeneratedRegex(@"^\s*\[\[\s*([A-Za-z0-9_.\-""]+)\s*\]\]\s*$")]
    private static partial Regex TomlArrayTable();

    [GeneratedRegex(@"^\s*([A-Za-z0-9_\-]+)\s*=\s*(.+?)\s*(#.*)?$")]
    private static partial Regex TomlKeyValue();

    /// <summary>
    /// Forge and NeoForge mods.toml. Only the parts we need: [[mods]] modId/displayName/version and
    /// [[dependencies.x]] modId with mandatory=true (Forge) or type="required" (NeoForge).
    /// </summary>
    static ModInfo Toml(string toml, string loader, string file, bool disabled, ZipArchive zip)
    {
        var tables = new List<(string Name, Dictionary<string, string> Values)>();
        var current = new Dictionary<string, string>();
        tables.Add(("", current));
        bool inMultiline = false;
        foreach (string raw in toml.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            // Skip multi-line strings (descriptions); count the ''' or """ markers.
            int marks = Regex.Matches(line, "'''|\"\"\"").Count;
            if (inMultiline) { if (marks % 2 == 1) inMultiline = false; continue; }
            if (marks % 2 == 1) { inMultiline = true; continue; }
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var t = TomlArrayTable().Match(line);
            if (t.Success)
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                tables.Add((t.Groups[1].Value.Replace("\"", ""), current));
                continue;
            }
            if (line.StartsWith('[')) { current = new(StringComparer.OrdinalIgnoreCase); tables.Add((line.Trim('[', ']', ' '), current)); continue; }
            var kv = TomlKeyValue().Match(line);
            if (kv.Success)
                current[kv.Groups[1].Value] = kv.Groups[2].Value.Trim().Trim('"', '\'');
        }

        var modTables = tables.Where(x => x.Name == "mods").Select(x => x.Values).ToList();
        var first = modTables.FirstOrDefault() ?? [];
        string id = first.GetValueOrDefault("modId") ?? Path.GetFileNameWithoutExtension(file);
        string? version = first.GetValueOrDefault("version");
        if (version is not null && version.Contains("${file.jarVersion}"))
            version = ManifestVersion(zip) ?? version;

        var requires = new List<string>();
        foreach (var (name, values) in tables.Where(x => x.Name.StartsWith("dependencies")))
        {
            if (values.GetValueOrDefault("modId") is not { } dep) continue;
            bool required = values.TryGetValue("type", out var type)
                ? type.Equals("required", StringComparison.OrdinalIgnoreCase)
                : values.GetValueOrDefault("mandatory")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            // Forge also uses side="CLIENT"/"SERVER"; either way the client needs it if it is mandatory.
            if (required) requires.Add(dep);
        }

        var provides = modTables.Select(m => m.GetValueOrDefault("modId")).OfType<string>().ToList();
        if (provides.Count == 0) provides.Add(id);
        return new ModInfo(file, id, first.GetValueOrDefault("displayName") ?? id, version, loader, requires, provides, disabled, null);
    }

    static string? ManifestVersion(ZipArchive zip)
    {
        string? manifest = Entry(zip, "META-INF/MANIFEST.MF");
        return manifest?.Replace("\r", "").Split('\n')
            .FirstOrDefault(l => l.StartsWith("Implementation-Version:"))?["Implementation-Version:".Length..].Trim();
    }

    /// <summary>Old Forge (1.12 and earlier): mcmod.info is a JSON list, or an object with "modList".</summary>
    static ModInfo McmodInfo(string json, string file, bool disabled)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("modList", out var ml) ? ml : default;
            if (list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
            {
                var m = list[0];
                string id = Str(m, "modid") ?? Path.GetFileNameWithoutExtension(file);
                var requires = m.TryGetProperty("requiredMods", out var rm) && rm.ValueKind == JsonValueKind.Array
                    ? rm.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Split('@')[0]).ToList()
                    : [];
                return new ModInfo(file, id, Str(m, "name") ?? id, Str(m, "version"), "forge", requires, [id], disabled, null);
            }
        }
        catch (JsonException) { }
        return new ModInfo(file, Path.GetFileNameWithoutExtension(file), file, null, "forge", [], [], disabled, null);
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string[] SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
