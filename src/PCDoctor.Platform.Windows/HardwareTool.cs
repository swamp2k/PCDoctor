using System.Management;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Core;

namespace PCDoctor.Platform.Windows;

/// <summary>system_hardware: CPU, GPU, RAM, OS, disks and motherboard via WMI (no shell).</summary>
[SupportedOSPlatform("windows")]
public sealed class HardwareTool : ITool
{
    public string Name => "system_hardware";
    public string Description =>
        "Read the PC's hardware and OS: CPU, graphics cards with driver versions, RAM, Windows version, disks with free space, motherboard, " +
        "and devices Windows reports a problem with (missing driver, disabled, failed to start).";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        var result = new Dictionary<string, object?>
        {
            ["cpu"] = Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, LoadPercentage FROM Win32_Processor", ct),
            ["gpu"] = Query("SELECT Name, DriverVersion, DriverDate, AdapterRAM, VideoModeDescription, Status FROM Win32_VideoController", ct),
            ["computer"] = Query("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem", ct),
            ["memory_modules"] = Query("SELECT Capacity, Speed, Manufacturer, PartNumber FROM Win32_PhysicalMemory", ct),
            ["os"] = Query("SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime, FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem", ct),
            ["disks"] = Query("SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3", ct),
            ["motherboard"] = Query("SELECT Manufacturer, Product FROM Win32_BaseBoard", ct),
            ["problem_devices"] = Query("SELECT Name, PNPClass, Status, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0 AND ConfigManagerErrorCode <> 45", ct),
            ["note"] = "Sizes are in bytes, except FreePhysicalMemory/TotalVisibleMemorySize (KB). GPU AdapterRAM is capped at 4 GB by Windows and may be wrong for larger cards. " +
                       "problem_devices: ConfigManagerErrorCode 28 = no driver installed, 22 = disabled, 10 = cannot start, 43 = stopped after reporting a problem. Devices that are just unplugged (code 45) are left out.",
        };
        return ToolResult.Json(result);
    }, ct);

    static object Query(string wql, CancellationToken ct)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            var rows = new List<Dictionary<string, object?>>();
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                ct.ThrowIfCancellationRequested();
                using (obj)
                {
                    var row = new Dictionary<string, object?>();
                    foreach (PropertyData p in obj.Properties)
                        row[p.Name] = p.Value is string or null ? p.Value : p.Value.ToString();
                    rows.Add(row);
                }
            }
            return rows;
        }
        catch (ManagementException ex)
        {
            return new { error = ex.Message };
        }
    }
}
