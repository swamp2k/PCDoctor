using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using PCDoctor.Core;

namespace PCDoctor.Platform.Linux;

/// <summary>
/// The Linux (and Steam Deck) version of the performance sampler. Reads the same plain text files
/// that Linux's own system monitors use: /proc/stat (CPU), /proc/meminfo (RAM), /proc/diskstats (disk),
/// /proc/net/dev (network) and /sys/class/drm (AMD graphics, which is what the Steam Deck has).
/// Anything this PC does not offer is reported as null.
/// </summary>
public sealed class LinuxPerfSampler : IPerfSampler, IDisposable
{
    readonly string _root;
    (long Busy, long Total) _cpuPrev = (-1, -1);
    long _diskPrevMs = -1, _netPrevBytes = -1, _prevTicks;
    readonly Ping _ping = new();
    static readonly IPAddress PingTarget = IPAddress.Parse("1.1.1.1");
    Task<PingReply>? _pingTask;
    Dictionary<int, (string Name, double CpuMs)> _procPrev = [];
    long _procPrevTicks = Stopwatch.GetTimestamp();

    /// <inheritdoc/>
    public string? Game { get; set; }

    /// <param name="root">Where /proc and /sys live. Only tests change it.</param>
    public LinuxPerfSampler(string root = "/") => _root = root;

    string P(string path) => Path.Combine(_root, path.TrimStart('/'));

    public PerfSample Read()
    {
        long now = Stopwatch.GetTimestamp();
        double wallMs = _prevTicks == 0 ? 0 : Stopwatch.GetElapsedTime(_prevTicks, now).TotalMilliseconds;
        _prevTicks = now;

        double? cpuFromStat = Cpu();
        var (ramPct, ramUsedGb) = Memory();
        var (topName, topCpu, allCpu, gameCpu, gameRam) = TopProcess();
        var (pingMs, pingLost) = PingResult();
        return new PerfSample(0, Math.Round(cpuFromStat ?? allCpu, 1), Gpu(), ramPct, ramUsedGb, Disk(wallMs), topName, topCpu,
            CpuSpeed(), GpuMemoryGb(), null, pingMs, pingLost, gameCpu, null, gameRam, NetworkMbps(wallMs));
    }

    /// <summary>Share of time the processor was not idle since the last reading (first line of /proc/stat).</summary>
    double? Cpu()
    {
        string? stat = LinuxFiles.Read(P("/proc/stat"));
        var parsed = ParseCpuLine(stat);
        if (parsed is null) return null;
        var prev = _cpuPrev;
        _cpuPrev = parsed.Value;
        if (prev.Total < 0 || parsed.Value.Total <= prev.Total) return null;
        return Clamp(100.0 * (parsed.Value.Busy - prev.Busy) / (parsed.Value.Total - prev.Total));
    }

    /// <summary>"cpu  user nice system idle iowait irq softirq steal ..." -> busy and total time.</summary>
    public static (long Busy, long Total)? ParseCpuLine(string? stat)
    {
        string? line = stat?.Split('\n').FirstOrDefault(l => l.StartsWith("cpu "));
        if (line is null) return null;
        long[] v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(x => long.TryParse(x, out long n) ? n : 0).ToArray();
        if (v.Length < 4) return null;
        long total = v.Sum();
        long idle = v[3] + (v.Length > 4 ? v[4] : 0); // idle + waiting for disk
        return (total - idle, total);
    }

    (double Percent, double UsedGb) Memory()
    {
        var m = LinuxFiles.KeyValues(LinuxFiles.Read(P("/proc/meminfo")), ':');
        double total = Kb(m, "MemTotal"), available = Kb(m, "MemAvailable");
        if (total <= 0) return (0, 0);
        double used = total - available;
        return (Math.Round(100 * used / total, 1), Math.Round(used / 1048576.0, 1));
    }

    static double Kb(Dictionary<string, string> m, string key) =>
        m.TryGetValue(key, out string? v) && double.TryParse(v.Split(' ')[0], out double kb) ? kb : 0;

    /// <summary>Busiest physical disk: the share of time it was working (the io_ticks column of /proc/diskstats).</summary>
    double? Disk(double wallMs)
    {
        long? ms = BusiestDiskMs(LinuxFiles.Read(P("/proc/diskstats")));
        if (ms is null) return null;
        long prev = _diskPrevMs;
        _diskPrevMs = ms.Value;
        if (prev < 0 || wallMs <= 0 || ms < prev) return null;
        return Math.Round(Clamp(100.0 * (ms.Value - prev) / wallMs), 1);
    }

    /// <summary>Highest io_ticks over whole disks (sda, nvme0n1, mmcblk0), not partitions or virtual devices.</summary>
    public static long? BusiestDiskMs(string? diskstats)
    {
        if (diskstats is null) return null;
        long? best = null;
        foreach (string line in diskstats.Split('\n'))
        {
            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 13 || !IsWholeDisk(f[2])) continue;
            if (long.TryParse(f[12], out long ticks) && (best is null || ticks > best)) best = ticks;
        }
        return best;
    }

    static bool IsWholeDisk(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^(sd[a-z]+|vd[a-z]+|nvme\d+n\d+|mmcblk\d+)$");

    double? NetworkMbps(double wallMs)
    {
        long? bytes = NetworkBytes(LinuxFiles.Read(P("/proc/net/dev")));
        if (bytes is null) return null;
        long prev = _netPrevBytes;
        _netPrevBytes = bytes.Value;
        if (prev < 0 || wallMs <= 0 || bytes < prev) return null;
        return Math.Round((bytes.Value - prev) * 8 / 1e6 / (wallMs / 1000), 2);
    }

    /// <summary>Bytes received plus sent over all network adapters except the internal "lo".</summary>
    public static long? NetworkBytes(string? netDev)
    {
        if (netDev is null) return null;
        long sum = 0;
        foreach (string line in netDev.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0 || line[..colon].Trim() == "lo") continue;
            string[] f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 9 && long.TryParse(f[0], out long rx) && long.TryParse(f[8], out long tx)) sum += rx + tx;
        }
        return sum;
    }

    /// <summary>AMD graphics (the Steam Deck, and most Linux gaming PCs) report how busy they are in gpu_busy_percent.</summary>
    double? Gpu()
    {
        double? best = null;
        foreach (string card in GraphicsCards())
            if (LinuxFiles.ReadLong(Path.Combine(card, "device", "gpu_busy_percent")) is { } pct && (best is null || pct > best))
                best = pct;
        return best is null ? null : Clamp(best.Value);
    }

    double? GpuMemoryGb()
    {
        long? bytes = null;
        foreach (string card in GraphicsCards())
            if (LinuxFiles.ReadLong(Path.Combine(card, "device", "mem_info_vram_used")) is { } b)
                bytes = (bytes ?? 0) + b;
        return bytes is null ? null : Math.Round(bytes.Value / 1073741824.0, 2);
    }

    IEnumerable<string> GraphicsCards() =>
        LinuxFiles.Dirs(P("/sys/class/drm")).Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^card\d+$"));

    /// <summary>Processor speed as % of its highest normal speed, averaged over all cores.</summary>
    double? CpuSpeed()
    {
        var pcts = new List<double>();
        foreach (string cpu in LinuxFiles.Dirs(P("/sys/devices/system/cpu")))
        {
            string freq = Path.Combine(cpu, "cpufreq");
            long? cur = LinuxFiles.ReadLong(Path.Combine(freq, "scaling_cur_freq"));
            long? max = LinuxFiles.ReadLong(Path.Combine(freq, "base_frequency")) ?? LinuxFiles.ReadLong(Path.Combine(freq, "cpuinfo_max_freq"));
            if (cur > 0 && max > 0) pcts.Add(100.0 * cur.Value / max.Value);
        }
        return pcts.Count == 0 ? null : Math.Round(pcts.Average(), 0);
    }

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

    /// <summary>The busiest program, all programs together, and (when a game is chosen) the game's own CPU and RAM.</summary>
    (string? Name, double Cpu, double AllCpu, double? GameCpu, double? GameRamGb) TopProcess()
    {
        long nowTicks = Stopwatch.GetTimestamp();
        double wallMs = Stopwatch.GetElapsedTime(_procPrevTicks, nowTicks).TotalMilliseconds;
        var now = new Dictionary<int, (string Name, double CpuMs)>();
        string? best = null, game = Game;
        double bestCpu = 0, all = 0, gameCpu = 0, gameRam = 0;
        bool running = false;
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    double ms = p.TotalProcessorTime.TotalMilliseconds;
                    now[p.Id] = (p.ProcessName, ms);
                    bool isGame = game is not null && p.ProcessName.Equals(game, StringComparison.OrdinalIgnoreCase);
                    if (isGame) { running = true; gameRam += p.WorkingSet64; }
                    if (wallMs > 0 && _procPrev.TryGetValue(p.Id, out var before))
                    {
                        double pct = (ms - before.CpuMs) / wallMs / Environment.ProcessorCount * 100;
                        all += Math.Max(0, pct);
                        if (pct > bestCpu) (best, bestCpu) = (p.ProcessName, pct);
                        if (isGame) gameCpu += Math.Max(0, pct);
                    }
                }
                catch { /* not ours to read, or exited meanwhile */ }
            }
        }
        _procPrev = now;
        _procPrevTicks = nowTicks;
        return (best, Math.Round(Clamp(bestCpu), 1), Math.Round(Clamp(all), 1),
            running ? Math.Round(Clamp(gameCpu), 1) : null, running ? Math.Round(gameRam / 1073741824.0, 2) : null);
    }

    static double Clamp(double v) => Math.Clamp(v, 0, 100);

    public void Dispose() => _ping.Dispose();
}
