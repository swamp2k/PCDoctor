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
    readonly PerformanceCounterCategory? _net = TryCategory("Network Interface");
    long _netBytes = -1, _netTicks;
    readonly Ping _ping = new();
    static readonly IPAddress PingTarget = IPAddress.Parse("1.1.1.1");
    Task<PingReply>? _pingTask;
    HashSet<int> _gamePids = [];

    /// <inheritdoc/>
    public string? Game { get; set; }

    Dictionary<string, CounterSample> _gpuPrev = [];
    Dictionary<int, (string Name, double CpuMs)> _procPrev = [];
    long _procPrevTicks = Stopwatch.GetTimestamp();

    public PerfSample Read()
    {
        double? disk = _diskIdle is null ? null : Clamp(100 - _diskIdle.NextValue());
        var (ramPct, ramUsedGb) = Memory();
        var (topName, topCpu, allCpu, gameCpu, gameRam) = TopProcess();
        // If the CPU counter is unavailable (e.g. renamed on a localized Windows), add up all processes instead.
        double cpu = _cpu is null ? allCpu : Clamp(_cpu.NextValue());
        var (gpu, topGpu, gameGpu) = Gpu();
        var (pingMs, pingLost) = PingResult();
        return new PerfSample(0, Math.Round(cpu, 1), gpu, ramPct, ramUsedGb, disk is null ? null : Math.Round(disk.Value, 1), topName, topCpu,
            CpuSpeed(), GpuMemoryGb(), topGpu, pingMs, pingLost, gameCpu, gameGpu, gameRam, NetworkMbps());
    }

    /// <summary>Traffic in and out over all network adapters since the last reading, in megabits per second.</summary>
    double? NetworkMbps()
    {
        if (_net is null)
            return null;
        try
        {
            long bytes = (long)_net.ReadCategory()["Bytes Total/sec"].Values.Cast<InstanceData>().Sum(d => (double)d.RawValue);
            long now = Stopwatch.GetTimestamp();
            double? mbps = null;
            if (_netBytes >= 0 && now > _netTicks && bytes >= _netBytes)
                mbps = Math.Round((bytes - _netBytes) * 8 / 1e6 / Stopwatch.GetElapsedTime(_netTicks, now).TotalSeconds, 2);
            (_netBytes, _netTicks) = (bytes, now);
            return mbps;
        }
        catch (Exception) { return null; }
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
    (double? Percent, string? TopProgram, double? Game) Gpu()
    {
        if (_gpu is null)
            return (null, null, null);
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
            double? game = Game is null || _gamePids.Count == 0 ? null : Math.Round(Clamp(perPid.Where(p => _gamePids.Contains(p.Key)).Sum(p => p.Value)), 1);
            return (Math.Round(Clamp(perEngine.Count == 0 ? 0 : perEngine.Values.Max()), 1), top, game);
        }
        catch (Exception)
        {
            return (null, null, null); // counter missing or driver does not report it
        }
    }

    /// <summary>The busiest program, all programs together, and (when a game is chosen) the game's own CPU and RAM.</summary>
    (string? Name, double Cpu, double AllCpu, double? GameCpu, double? GameRamGb) TopProcess()
    {
        long nowTicks = Stopwatch.GetTimestamp();
        double wallMs = Stopwatch.GetElapsedTime(_procPrevTicks, nowTicks).TotalMilliseconds;
        var now = new Dictionary<int, (string Name, double CpuMs)>();
        string? best = null;
        double bestCpu = 0, all = 0;
        string? game = Game;
        var gamePids = new HashSet<int>();
        double gameCpu = 0, gameRam = 0;
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0) continue; // "Idle" is not a real program
                try
                {
                    double ms = p.TotalProcessorTime.TotalMilliseconds;
                    now[p.Id] = (p.ProcessName, ms);
                    // Some games run as several processes with the same name (Roblox, browsers); add them up.
                    bool isGame = game is not null && p.ProcessName.Equals(game, StringComparison.OrdinalIgnoreCase);
                    if (isGame)
                    {
                        gamePids.Add(p.Id);
                        gameRam += p.WorkingSet64;
                    }
                    if (wallMs > 0 && _procPrev.TryGetValue(p.Id, out var before))
                    {
                        double pct = (ms - before.CpuMs) / wallMs / Environment.ProcessorCount * 100;
                        all += Math.Max(0, pct);
                        if (pct > bestCpu) (best, bestCpu) = (p.ProcessName, pct);
                        if (isGame) gameCpu += Math.Max(0, pct);
                    }
                }
                catch { /* access denied or exited meanwhile */ }
            }
        }
        _procPrev = now;
        _procPrevTicks = nowTicks;
        _gamePids = gamePids;
        // A chosen game that is not running gives null, so a closed or crashed game shows as a gap.
        bool running = game is not null && gamePids.Count > 0;
        return (best, Math.Round(Clamp(bestCpu), 1), Math.Round(Clamp(all), 1),
            running ? Math.Round(Clamp(gameCpu), 1) : null, running ? Math.Round(gameRam / 1073741824.0, 2) : null);
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
