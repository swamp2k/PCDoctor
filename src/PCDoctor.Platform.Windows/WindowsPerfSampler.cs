using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCDoctor.Core;

namespace PCDoctor.Platform.Windows;

/// <summary>
/// Reads CPU, GPU, RAM and disk use from Windows performance counters, plus the process using most CPU.
/// The same sources Task Manager uses. Any counter Windows does not offer is reported as null.
/// Also: processor speed, graphics memory, the program using the graphics card most, and a ping to the internet.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPerfSampler : IPerfSampler, IDisposable
{
    readonly PerformanceCounter? _cpu =
        TryCounter("Processor Information", "% Processor Utility", "_Total") ?? TryCounter("Processor", "% Processor Time", "_Total");
    readonly PerformanceCounter? _diskIdle = TryCounter("PhysicalDisk", "% Idle Time", "_Total");
    readonly PerformanceCounter? _cpuSpeed = TryCounter("Processor Information", "% Processor Performance", "_Total");
    readonly PerformanceCounterCategory? _gpu = TryCategory("GPU Engine");
    readonly PerformanceCounterCategory? _gpuMemory = TryCategory("GPU Adapter Memory");
    readonly Ping _ping = new();
    static readonly IPAddress PingTarget = IPAddress.Parse("1.1.1.1");
    Task<PingReply>? _pingTask;

    Dictionary<string, CounterSample> _gpuPrev = [];
    Dictionary<int, (string Name, double CpuMs)> _procPrev = [];
    long _procPrevTicks = Stopwatch.GetTimestamp();

    public PerfSample Read()
    {
        double? disk = _diskIdle is null ? null : Clamp(100 - _diskIdle.NextValue());
        var (ramPct, ramUsedGb) = Memory();
        var (topName, topCpu, allCpu) = TopProcess();
        // If the CPU counter is unavailable (e.g. renamed on a localized Windows), add up all processes instead.
        double cpu = _cpu is null ? allCpu : Clamp(_cpu.NextValue());
        var (gpu, topGpu) = Gpu();
        var (pingMs, pingLost) = PingResult();
        return new PerfSample(0, Math.Round(cpu, 1), gpu, ramPct, ramUsedGb, disk is null ? null : Math.Round(disk.Value, 1), topName, topCpu,
            CpuSpeed(), GpuMemoryGb(), topGpu, pingMs, pingLost);
    }

    double? CpuSpeed()
    {
        try { return _cpuSpeed is null ? null : Math.Round(_cpuSpeed.NextValue(), 0); }
        catch (Exception) { return null; }
    }

    /// <summary>Dedicated graphics memory in use, added up over all graphics adapters.</summary>
    double? GpuMemoryGb()
    {
        if (_gpuMemory is null)
            return null;
        try
        {
            double bytes = _gpuMemory.ReadCategory()["Dedicated Usage"].Values.Cast<InstanceData>().Sum(d => (double)d.RawValue);
            return Math.Round(bytes / 1073741824.0, 2);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// One ping per sample, answered by the next sample, so a slow answer never delays the recording.
    /// Null while a ping is still on its way.
    /// </summary>
    (double? Ms, bool Lost) PingResult()
    {
        double? ms = null;
        bool lost = false;
        if (_pingTask is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully && done.Result.Status == IPStatus.Success) ms = done.Result.RoundtripTime;
            else lost = true;
            _pingTask = null;
        }
        if (_pingTask is null)
        {
            try { _pingTask = _ping.SendPingAsync(PingTarget, 1000); }
            catch (Exception) { lost = true; }
        }
        return (ms, lost);
    }

    /// <summary>
    /// GPU use like Task Manager shows it: add up all processes per engine type (3D, video decode, ...)
    /// and take the busiest engine type.
    /// </summary>
    /// <remarks>Instance names look like "pid_1234_luid_0x..._phys_0_eng_0_engtype_3D", so the program is known too.</remarks>
    (double? Percent, string? TopProgram) Gpu()
    {
        if (_gpu is null)
            return (null, null);
        try
        {
            InstanceDataCollection util = _gpu.ReadCategory()["Utilization Percentage"];
            var now = new Dictionary<string, CounterSample>();
            var perEngine = new Dictionary<string, double>();
            var perPid = new Dictionary<int, double>();
            foreach (InstanceData d in util.Values)
            {
                now[d.InstanceName] = d.Sample;
                if (!_gpuPrev.TryGetValue(d.InstanceName, out var before))
                    continue;
                double value = CounterSample.Calculate(before, d.Sample);
                int at = d.InstanceName.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                string engine = at < 0 ? "other" : d.InstanceName[(at + 8)..];
                perEngine[engine] = perEngine.GetValueOrDefault(engine) + value;
                if (d.InstanceName.StartsWith("pid_") && int.TryParse(d.InstanceName.AsSpan(4, Math.Max(0, d.InstanceName.IndexOf('_', 4) - 4)), out int pid))
                    perPid[pid] = perPid.GetValueOrDefault(pid) + value;
            }
            _gpuPrev = now;
            string? top = null;
            if (perPid.Count > 0)
            {
                var best = perPid.MaxBy(p => p.Value);
                if (best.Value >= 1 && _procPrev.TryGetValue(best.Key, out var proc)) top = proc.Name;
            }
            return (Math.Round(Clamp(perEngine.Count == 0 ? 0 : perEngine.Values.Max()), 1), top);
        }
        catch (Exception)
        {
            return (null, null); // counter missing or driver does not report it
        }
    }

    (string? Name, double Cpu, double AllCpu) TopProcess()
    {
        long nowTicks = Stopwatch.GetTimestamp();
        double wallMs = Stopwatch.GetElapsedTime(_procPrevTicks, nowTicks).TotalMilliseconds;
        var now = new Dictionary<int, (string Name, double CpuMs)>();
        string? best = null;
        double bestCpu = 0, all = 0;
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
                        all += Math.Max(0, pct);
                        if (pct > bestCpu) (best, bestCpu) = (p.ProcessName, pct);
                    }
                }
                catch { /* access denied or exited meanwhile */ }
            }
        }
        _procPrev = now;
        _procPrevTicks = nowTicks;
        return (best, Math.Round(Clamp(bestCpu), 1), Math.Round(Clamp(all), 1));
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

    static PerformanceCounterCategory? TryCategory(string name)
    {
        try { return PerformanceCounterCategory.Exists(name) ? new PerformanceCounterCategory(name) : null; }
        catch (Exception) { return null; }
    }

    static double Clamp(double v) => Math.Clamp(v, 0, 100);

    public void Dispose()
    {
        _cpu?.Dispose();
        _cpuSpeed?.Dispose();
        _diskIdle?.Dispose();
        _ping.Dispose();
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
