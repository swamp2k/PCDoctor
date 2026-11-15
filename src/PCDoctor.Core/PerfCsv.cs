using System.Globalization;
using System.Text;

namespace PCDoctor.Core;

/// <summary>Turns a recording into a CSV file that opens in Excel (one row per second).</summary>
public static class PerfCsv
{
    static readonly string[] Columns =
    [
        "Seconds", "CPU %", "GPU %", "RAM %", "RAM used GB", "Disk %", "Top process", "Top process CPU %",
        "CPU speed %", "GPU memory GB", "Top GPU process", "Ping ms", "Ping lost",
        "Game CPU %", "Game GPU %", "Game RAM GB", "Network Mbit/s",
        "GPU temp C", "GPU power W", "GPU clock MHz", "GPU throttle", "GPU driver reply ms",
    ];

    /// <summary>
    /// <paramref name="culture"/> decides the separator and decimal mark; the default is this PC's own settings,
    /// because that is what Excel on this PC expects (Danish Excel wants ';' and a decimal comma).
    /// </summary>
    public static string Build(PerfRecording r, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string sep = culture.TextInfo.ListSeparator.Trim() is { Length: > 0 } s ? s : ";";
        var sb = new StringBuilder();
        sb.Append(string.Join(sep, Columns.Select(c => Field(c, sep)))).Append("\r\n");
        foreach (var x in r.Samples)
        {
            string?[] cells =
            [
                Num(x.Seconds, culture), Num(x.Cpu, culture), Num(x.Gpu, culture), Num(x.Ram, culture), Num(x.RamUsedGb, culture),
                Num(x.Disk, culture), x.TopProcess, Num(x.TopProcessCpu, culture), Num(x.CpuSpeed, culture),
                Num(x.GpuMemoryGb, culture), x.TopGpuProcess, Num(x.PingMs, culture), x.PingLost ? "1" : "0",
                Num(x.GameCpu, culture), Num(x.GameGpu, culture), Num(x.GameRamGb, culture), Num(x.NetMbps, culture),
                Num(x.GpuTempC, culture), Num(x.GpuPowerW, culture), Num(x.GpuClockMhz, culture), x.GpuThrottle,
                x.GpuQueryMs?.ToString(culture),
            ];
            sb.Append(string.Join(sep, cells.Select(c => Field(c, sep)))).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>A name for the file, e.g. "pcdoctor-20261115-150312.csv".</summary>
    public static string FileName(PerfRecording r) => $"pcdoctor-{r.Started.LocalDateTime:yyyyMMdd-HHmmss}.csv";

    static string? Num(double? v, CultureInfo c) => v?.ToString("0.##", c);

    static string Field(string? v, string sep) =>
        v is null ? "" : v.Contains(sep) || v.Contains('"') || v.Contains('\n') || v.Contains('\r') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
}
