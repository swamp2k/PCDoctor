using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Core;

namespace PCDoctor.Platform.Linux;

/// <summary>
/// system_hardware on Linux: the same facts as on Windows (processor, graphics, memory, system version, disks),
/// read from /proc and /sys, plus temperatures and battery, which matter a lot on a Steam Deck.
/// The field names match the Windows tool, so the Diagnostics page reads both the same way.
/// </summary>
public sealed class LinuxHardwareTool(string root = "/") : ITool
{
    public string Name => "system_hardware";
    public string Description =>
        "Read this Linux PC's hardware and system: processor, graphics card and driver, memory, Linux distribution and kernel version, " +
        "computer model (a Steam Deck shows as Valve Jupiter/Galileo), disks with free space, temperatures and battery health.";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    string P(string path) => Path.Combine(root, path.TrimStart('/'));

    public Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        var meminfo = LinuxFiles.KeyValues(LinuxFiles.Read(P("/proc/meminfo")), ':');
        long memBytes = meminfo.TryGetValue("MemTotal", out var mt) && long.TryParse(mt.Split(' ')[0], out long kb) ? kb * 1024 : 0;
        string vendor = LinuxFiles.Read(P("/sys/class/dmi/id/sys_vendor")) ?? "";
        string product = LinuxFiles.Read(P("/sys/class/dmi/id/product_name")) ?? "";
        var result = new Dictionary<string, object?>
        {
            ["platform"] = "Linux",
            ["cpu"] = new[] { Cpu() },
            ["gpu"] = Gpus(),
            ["computer"] = new[] { new Dictionary<string, object?>
            {
                ["Manufacturer"] = vendor,
                ["Model"] = FriendlyModel(vendor, product),
                ["TotalPhysicalMemory"] = memBytes.ToString(),
            } },
            ["os"] = new[] { Os() },
            ["disks"] = Disks(),
            ["temperatures"] = Temperatures(),
            ["battery"] = Batteries(),
            ["note"] = "Sizes are in bytes. temperatures are in °C; a processor or graphics chip above about 90 °C is hot. " +
                       "battery health_percent is how much the battery holds compared with when it was new.",
        };
        return ToolResult.Json(result);
    }, ct);

    /// <summary>Valve's code names for the Steam Deck.</summary>
    public static string FriendlyModel(string vendor, string product) => (vendor.Trim(), product.Trim()) switch
    {
        ("Valve", "Jupiter") => "Steam Deck (LCD)",
        ("Valve", "Galileo") => "Steam Deck (OLED)",
        _ => product,
    };

    Dictionary<string, object?> Cpu()
    {
        string text = LinuxFiles.Read(P("/proc/cpuinfo")) ?? "";
        var first = LinuxFiles.KeyValues(text, ':');
        int threads = text.Split('\n').Count(l => l.StartsWith("processor"));
        var cores = text.Split("\n\n").Select(b => LinuxFiles.KeyValues(b, ':'))
            .Select(b => (b.GetValueOrDefault("physical id"), b.GetValueOrDefault("core id"))).Where(c => c.Item2 is not null).Distinct().Count();
        return new()
        {
            ["Name"] = first.GetValueOrDefault("model name") ?? first.GetValueOrDefault("Model") ?? "?",
            ["NumberOfCores"] = (cores > 0 ? cores : threads).ToString(),
            ["NumberOfLogicalProcessors"] = threads.ToString(),
        };
    }

    List<Dictionary<string, object?>> Gpus()
    {
        var list = new List<Dictionary<string, object?>>();
        var names = GpuNamesFromLspci();
        foreach (string card in LinuxFiles.Dirs(P("/sys/class/drm")))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(card), @"^card\d+$")) continue;
            string device = Path.Combine(card, "device");
            string? driver = null;
            try
            {
                var link = new FileInfo(Path.Combine(device, "driver")).LinkTarget;
                if (link is not null) driver = Path.GetFileName(link);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            string vendorId = LinuxFiles.Read(Path.Combine(device, "vendor")) ?? "";
            string vendorName = vendorId switch { "0x1002" => "AMD", "0x10de" => "NVIDIA", "0x8086" => "Intel", _ => vendorId };
            long? vram = LinuxFiles.ReadLong(Path.Combine(device, "mem_info_vram_total"));
            list.Add(new()
            {
                ["Name"] = names.Count > list.Count ? names[list.Count] : $"{vendorName} graphics",
                ["DriverVersion"] = driver,
                ["VramBytes"] = vram?.ToString(),
            });
        }
        return list;
    }

    /// <summary>The graphics card's real name, from lspci when it is installed (it is on SteamOS and most desktops).</summary>
    static List<string> GpuNamesFromLspci()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("lspci") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return [];
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output.Split('\n')
                .Where(l => l.Contains("VGA compatible") || l.Contains("3D controller") || l.Contains("Display controller"))
                .Select(l => l[(l.IndexOf(": ", StringComparison.Ordinal) + 2)..].Trim()).ToList();
        }
        catch (Exception) { return []; }
    }

    Dictionary<string, object?> Os()
    {
        var rel = LinuxFiles.KeyValues(LinuxFiles.Read(P("/etc/os-release")), '=');
        double uptimeHours = double.TryParse(LinuxFiles.Read(P("/proc/uptime"))?.Split(' ')[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double s) ? Math.Round(s / 3600, 1) : 0;
        return new()
        {
            ["Caption"] = rel.GetValueOrDefault("PRETTY_NAME") ?? rel.GetValueOrDefault("NAME") ?? "Linux",
            ["Version"] = rel.GetValueOrDefault("VERSION_ID"),
            ["Kernel"] = LinuxFiles.Read(P("/proc/sys/kernel/osrelease")),
            ["OSArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ["UptimeHours"] = uptimeHours,
        };
    }

    /// <summary>The drives a person cares about: the system, the home folder, and memory cards / extra disks.</summary>
    static List<Dictionary<string, object?>> Disks()
    {
        var list = new List<Dictionary<string, object?>>();
        var seen = new HashSet<(long, long)>();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                string m = d.Name.TrimEnd('/');
                if (m.Length == 0) m = "/";
                bool wanted = m is "/" or "/home" || m.StartsWith("/run/media/") || m.StartsWith("/media/") || m.StartsWith("/mnt/");
                if (!wanted || !d.IsReady || d.TotalSize == 0 || !seen.Add((d.TotalSize, d.TotalFreeSpace))) continue;
                list.Add(new()
                {
                    ["DeviceID"] = m,
                    ["FileSystem"] = d.DriveFormat,
                    ["Size"] = d.TotalSize.ToString(),
                    ["FreeSpace"] = d.AvailableFreeSpace.ToString(),
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>Every temperature sensor (hwmon), e.g. the processor (k10temp, coretemp), graphics (amdgpu) and disk (nvme).</summary>
    List<object> Temperatures()
    {
        var list = new List<object>();
        foreach (string hw in LinuxFiles.Dirs(P("/sys/class/hwmon")))
        {
            string sensor = LinuxFiles.Read(Path.Combine(hw, "name")) ?? Path.GetFileName(hw);
            string[] inputs;
            try { inputs = Directory.GetFiles(hw, "temp*_input"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (string input in inputs.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (LinuxFiles.ReadLong(input) is not { } milli || milli <= 0) continue;
                string label = LinuxFiles.Read(input.Replace("_input", "_label")) ?? Path.GetFileName(input).Replace("_input", "");
                list.Add(new { sensor, label, celsius = Math.Round(milli / 1000.0, 0) });
            }
        }
        return list;
    }

    List<object> Batteries()
    {
        var list = new List<object>();
        foreach (string ps in LinuxFiles.Dirs(P("/sys/class/power_supply")))
        {
            if (LinuxFiles.Read(Path.Combine(ps, "type")) != "Battery") continue;
            long? full = LinuxFiles.ReadLong(Path.Combine(ps, "energy_full")) ?? LinuxFiles.ReadLong(Path.Combine(ps, "charge_full"));
            long? design = LinuxFiles.ReadLong(Path.Combine(ps, "energy_full_design")) ?? LinuxFiles.ReadLong(Path.Combine(ps, "charge_full_design"));
            list.Add(new
            {
                name = Path.GetFileName(ps),
                charge_percent = LinuxFiles.ReadLong(Path.Combine(ps, "capacity")),
                status = LinuxFiles.Read(Path.Combine(ps, "status")),
                health_percent = full > 0 && design > 0 ? Math.Round(100.0 * full.Value / design.Value, 0) : (double?)null,
            });
        }
        return list;
    }
}
