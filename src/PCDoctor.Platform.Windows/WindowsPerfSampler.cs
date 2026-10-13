using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCDoctor.Core;

namespace PCDoctor.Platform.Windows;

/// <summary>
/// Reads CPU, GPU, RAM and disk use from Windows performance counters, plus the process using most CPU.
/// The same sources Task Manager uses. Any counter Windows does not offer is reported as null.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPerfSampler : IPerfSampler, IDisposable
{
    readonly PerformanceCounter? _cpu =
        TryCounter("Processor Information", "% Processor Utility", "_Total") ?? TryCounter("Processor", "% Processor Time", "_Total");
    readonly PerformanceCounter? _diskIdle = TryCounter("PhysicalDisk", "% Idle Time", "_Total");
    readonly PerformanceCounterCategory? _gpu =
        PerformanceCounterCategory.Exists("GPU Engine") ? new PerformanceCounterCategory("GPU Engine") : null;

    Dictionary<string, CounterSample> _gpuPrev = [];
    Dictionary<int, (string Name, double CpuMs)> _procPrev = [];
    long _procPrevTicks = Stopwatch.GetTimestamp();

    public PerfSample Read()
    {
        double cpu = _cpu is null ? 0 : Clamp(_cpu.NextValue());
        double? disk = _diskIdle is null ? null : Clamp(100 - _diskIdle.NextValue());
        var (ramPct, ramUsedGb) = Memory();
        var (topName, topCpu) = TopProcess();
        return new PerfSample(0, Math.Round(cpu, 1), Gpu(), ramPct, ramUsedGb, disk is null ? null : Math.Round(disk.Value, 1), topName, topCpu);
    }

    /// <summary>
    /// GPU use like Task Manager shows it: add up all processes per engine type (3D, video decode, ...)
    /// and take the busiest engine type.
    /// </summary>
    double? Gpu()
    {
        if (_gpu is null)
            return null;
        try
        {
            InstanceDataCollection util = _gpu.ReadCategory()["Utilization Percentage"];
            var now = new Dictionary<string, CounterSample>();
            var perEngine = new Dictionary<string, double>();
            foreach (InstanceData d in util.Values)
            {
                now[d.InstanceName] = d.Sample;
                if (!_gpuPrev.TryGetValue(d.InstanceName, out var before))
                    continue;
                int at = d.InstanceName.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                string engine = at < 0 ? "other" : d.InstanceName[(at + 8)..];
                perEngine[engine] = perEngine.GetValueOrDefault(engine) + CounterSample.Calculate(before, d.Sample);
            }
            _gpuPrev = now;
            return Math.Round(Clamp(perEngine.Count == 0 ? 0 : perEngine.Values.Max()), 1);
        }
        catch (Exception)
        {
            return null; // counter missing or driver does not report it
        }
    }

    (string? Name, double Cpu) TopProcess()
    {
        long nowTicks = Stopwatch.GetTimestamp();
        double wallMs = Stopwatch.GetElapsedTime(_procPrevTicks, nowTicks).TotalMilliseconds;
        var now = new Dictionary<int, (string Name, double CpuMs)>();
        string? best = null;
        double bestCpu = 0;
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0) continue; // "Idle" is not a real program
                try
                {
                    double ms = p.TotalProcessorTime.TotalMilliseconds;
                    now[p.Id] = (p.ProcessName, ms);
                    if (wallMs > 0 && _procPrev.TryGetValue(p.Id, out var before))
                    {
                        double pct = (ms - before.CpuMs) / wallMs / Environment.ProcessorCount * 100;
                        if (pct > bestCpu) (best, bestCpu) = (p.ProcessName, pct);
                    }
                }
                catch { /* access denied or exited meanwhile */ }
            }
        }
        _procPrev = now;
        _procPrevTicks = nowTicks;
        return (best, Math.Round(Clamp(bestCpu), 1));
    }

    static (double Percent, double UsedGb) Memory()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m) || m.TotalPhys == 0)
            return (0, 0);
        double used = m.TotalPhys - m.AvailPhys;
        return (Math.Round(100 * used / m.TotalPhys, 1), Math.Round(used / 1073741824.0, 1));
    }

    static PerformanceCounter? TryCounter(string category, string counter, string instance)
    {
        try
        {
            var c = new PerformanceCounter(category, counter, instance, readOnly: true);
            c.NextValue();
            return c;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static double Clamp(double v) => Math.Clamp(v, 0, 100);

    public void Dispose()
    {
        _cpu?.Dispose();
        _diskIdle?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
