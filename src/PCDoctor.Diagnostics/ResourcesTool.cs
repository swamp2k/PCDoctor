using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Core;

namespace PCDoctor.Diagnostics;

/// <summary>system_resources: how busy the PC is right now (CPU, GPU, RAM, disk) and how full the drives are.</summary>
/// <param name="samplerFactory">Makes a fresh sampler per call (it keeps state between readings). Null result = no counters on this OS.</param>
public sealed class ResourcesTool(Func<IPerfSampler?> samplerFactory) : ITool
{
    public string Name => "system_resources";
    public string Description =>
        "Measure how busy the PC is right now over about one second: CPU, graphics card (GPU), RAM and disk activity in percent, " +
        "the program using the most CPU, free space on each drive, and time since the last restart. " +
        "For a problem that comes and goes (for example while gaming), use performance_recording instead.";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public async Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct)
    {
        PerfSample? now = null;
        string? samplerError = null;
        IPerfSampler? sampler = null;
        try
        {
            sampler = samplerFactory();
            if (sampler is not null)
            {
                // Rate counters need two readings to compare.
                sampler.Read();
                await Task.Delay(1000, ct);
                now = sampler.Read();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            samplerError = e.Message;
        }
        finally
        {
            (sampler as IDisposable)?.Dispose();
        }

        double totalRamGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0, 1);
        return ToolResult.Json(new
        {
            cpu_percent = now?.Cpu,
            gpu_percent = now?.Gpu,
            ram_percent = now?.Ram,
            ram_used_gb = now?.RamUsedGb,
            ram_total_gb = totalRamGb,
            disk_active_percent = now?.Disk,
            top_process = now?.TopProcess,
            top_process_cpu_percent = now?.TopProcess is null ? null : now?.TopProcessCpu,
            uptime_hours = Math.Round(Environment.TickCount64 / 3_600_000.0, 1),
            drives = Drives(),
            measurement_error = samplerError,
            note = now is null && samplerError is null
                ? "Live CPU/GPU/RAM/disk counters are only available on Windows."
                : "Percentages are 0-100 for the whole machine. gpu/disk are null if Windows has no counter for them. " +
                  "low_space is true when a drive has less than 10 % or 10 GB free.",
        });
    }

    static List<object> Drives()
    {
        var list = new List<object>();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize == 0)
                    continue;
                double total = d.TotalSize / 1073741824.0, free = d.AvailableFreeSpace / 1073741824.0;
                list.Add(new
                {
                    drive = d.Name,
                    label = string.IsNullOrEmpty(d.VolumeLabel) ? null : d.VolumeLabel,
                    total_gb = Math.Round(total, 0),
                    free_gb = Math.Round(free, 1),
                    free_percent = Math.Round(100 * free / total, 0),
                    low_space = free / total < 0.10 || free < 10,
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* drive vanished or locked */ }
        }
        return list;
    }
}
