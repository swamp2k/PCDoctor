using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Core;

namespace PCDoctor.Diagnostics;

/// <summary>minecraft_mods: lists the mods of a Minecraft install and finds missing, duplicate and wrong-loader mods.</summary>
public sealed class MinecraftModsTool(MinecraftLocator? locator = null) : ITool
{
    /// <summary>The mod list sent to the model is cut at this many mods; problems are always all sent.</summary>
    const int MaxModsListed = 150;

    readonly MinecraftLocator _locator = locator ?? new MinecraftLocator();

    public string Name => "minecraft_mods";
    public string Description =>
        "Check the mods of a Minecraft install: reads every jar in its mods folder (Fabric, Quilt, Forge, NeoForge) and reports the loader, " +
        "each mod's id, name and version, and problems: a required mod that is missing, the same mod twice, a mod made for another loader, " +
        "or a damaged jar. Without 'folder' it checks the most recently used install that has mods. " +
        "Use it after gaming_logs when a crash names a mod or when a modded game will not start.";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["folder"] = new JsonObject { ["type"] = "string", ["description"] = "An install 'folder' from gaming_logs. Other paths are refused." },
        },
    };

    public Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        var installs = _locator.Find();
        string? folder = input.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

        MinecraftInstall? install;
        if (folder is not null)
        {
            // Only folders the locator found itself; never a path the model made up.
            install = installs.FirstOrDefault(i => string.Equals(Path.GetFullPath(i.Folder), LogFiles.SafeFullPath(folder), StringComparison.OrdinalIgnoreCase));
            if (install is null)
                return ToolResult.Error("That folder is not a Minecraft install found by gaming_logs. Call gaming_logs to see the list.");
        }
        else
        {
            install = installs.FirstOrDefault(i => i.ModCount > 0);
            if (install is null)
                return ToolResult.Json(new
                {
                    installs = installs.Select(i => new { i.Launcher, i.Name, i.Folder }),
                    note = installs.Count == 0
                        ? "No Minecraft installation was found for this user."
                        : "None of the Minecraft installs has mods; they run plain (vanilla) Minecraft.",
                });
        }

        ct.ThrowIfCancellationRequested();
        var report = MinecraftMods.Read(install.Folder);
        return ToolResult.Json(new
        {
            launcher = install.Launcher,
            name = install.Name,
            folder = report.Folder,
            loader = report.Loader,
            mods = report.ModCount,
            disabled = report.DisabledCount,
            problems = report.Problems,
            mod_list = report.Mods.Take(MaxModsListed).Select(m => new
            {
                file = m.File, id = m.Id, name = m.Name, version = m.Version, loader = m.Loader,
                requires = m.Requires.Count == 0 ? null : m.Requires,
                disabled = m.Disabled ? true : (bool?)null,
            }),
            list_cut = report.Mods.Count > MaxModsListed ? $"Only the first {MaxModsListed} of {report.Mods.Count} mods are listed." : null,
            other_installs = installs.Where(i => i != install && i.ModCount > 0).Select(i => new { i.Launcher, i.Name, i.Folder, mods = i.ModCount }),
            note = "loader is what most mods are made for. missing_dependency means a mod lists another mod as required and it is not in the folder " +
                   "(versions are not compared). A problem found here is a likely cause of a crash at start-up, but only blame it if the crash log fits. " +
                   ".jar.disabled files are switched off and ignored by the game.",
        });
    }, ct);
}
