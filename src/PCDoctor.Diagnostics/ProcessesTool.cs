using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Core;

namespace PCDoctor.Diagnostics;

/// <summary>system_processes: which programs use the most CPU and memory right now.</summary>
public sealed class ProcessesTool : ITool
{
    public string Name => "system_processes";
    public string Description =>
        "List running processes with CPU use (measured over about one second) and memory use. " +
        "Returns the top processes by CPU and by memory.";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["top"] = new JsonObject { ["type"] = "integer", ["description"] = "How many processes per list, 5-50. Default 15." },
        },
    };

    public async Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct)
    {
        int top = input.TryGetProperty("top", out var t) && t.TryGetInt32(out int n) ? Math.Clamp(n, 5, 50) : 15;

        var first = Snapshot();
        var sw = Stopwatch.StartNew();
        await Task.Delay(1000, ct);
        var second = Snapshot();
        double wallMs = sw.Elapsed.TotalMilliseconds;
        int cores = Environment.ProcessorCount;

        var rows = second.Values.Select(s =>
        {
            double cpu = first.TryGetValue(s.Pid, out var f)
                ? Math.Max(0, (s.CpuMs - f.CpuMs) / wallMs / cores * 100)
                : 0;
            return new { name = s.Name, pid = s.Pid, cpu_percent = Math.Round(cpu, 1), memory_mb = Math.Round(s.MemBytes / 1048576.0, 0) };
        }).ToList();

        return ToolResult.Json(new
        {
            logical_cores = cores,
            process_count = rows.Count,
            total_cpu_percent = Math.Round(rows.Sum(r => r.cpu_percent), 1),
            top_by_cpu = rows.OrderByDescending(r => r.cpu_percent).Take(top),
            top_by_memory = rows.OrderByDescending(r => r.memory_mb).Take(top),
            note = "cpu_percent is share of the whole machine. Processes the current user may not inspect show 0.",
        });
    }

    sealed record Sample(int Pid, string Name, double CpuMs, long MemBytes);

    static Dictionary<int, Sample> Snapshot()
    {
        var result = new Dictionary<int, Sample>();
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    double cpu = 0;
                    try { cpu = p.TotalProcessorTime.TotalMilliseconds; } catch { /* access denied for system processes */ }
                    result[p.Id] = new Sample(p.Id, p.ProcessName, cpu, p.WorkingSet64);
                }
                catch { /* process exited meanwhile */ }
            }
        }
        return result;
    }
}
