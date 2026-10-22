using System.Diagnostics;

namespace PCDoctor.Diagnostics;

/// <summary>One running program (all its processes added together) and what it uses right now.</summary>
public sealed record ActiveProgram(string Name, double CpuPercent, double RamMb, int Processes);

/// <summary>
/// Lists every running program with its CPU and RAM, for the "Live View" page.
/// CPU is measured between two calls, so the first call shows 0 % for everything.
/// </summary>
public sealed class ActivePrograms
{
    Dictionary<int, TimeSpan> _lastCpu = [];
    DateTime _lastTime = DateTime.UtcNow;

    public List<ActiveProgram> Sample()
    {
        var now = DateTime.UtcNow;
        double seconds = Math.Max(0.1, (now - _lastTime).TotalSeconds);
        var cpuNow = new Dictionary<int, TimeSpan>();
        var byName = new Dictionary<string, (double cpu, double ram, int count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == 0 || p.ProcessName == "Idle")
                        continue;
                    // Linux lists the kernel's own workers as processes; they use no memory of their own and are not programs.
                    if (OperatingSystem.IsLinux() && p.WorkingSet64 == 0)
                        continue;
                    double ram = p.WorkingSet64 / (1024.0 * 1024);
                    double cpu = 0;
                    try
                    {
                        var used = p.TotalProcessorTime;
                        cpuNow[p.Id] = used;
                        if (_lastCpu.TryGetValue(p.Id, out var before))
                            cpu = Math.Max(0, (used - before).TotalSeconds / seconds / Environment.ProcessorCount * 100);
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                    byName.TryGetValue(p.ProcessName, out var t);
                    byName[p.ProcessName] = (t.cpu + cpu, t.ram + ram, t.count + 1);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }

        _lastCpu = cpuNow;
        _lastTime = now;
        return byName.Select(kv => new ActiveProgram(kv.Key, Math.Min(100, kv.Value.cpu), kv.Value.ram, kv.Value.count))
            .OrderByDescending(x => Math.Round(x.CpuPercent, 1)).ThenByDescending(x => x.RamMb).ToList();
    }
}
