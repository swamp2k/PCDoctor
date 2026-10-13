using System.Diagnostics;
using System.Text;

namespace PCDoctor.Core;

/// <summary>What happened when a process ran.</summary>
public sealed record ProcessOutcome(
    int? ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    bool Truncated,
    string? FullOutputFile,
    double DurationSeconds);

/// <summary>
/// Runs a child process with a time limit, Stop support (kills the whole process tree),
/// captured stdout/stderr and capped output. Never elevates: no shell-execute, no "runas".
/// </summary>
public static class ProcessRunner
{
    /// <summary>Hard memory cap per stream; anything past this is dropped even from the saved file.</summary>
    const int HardCapChars = 5_000_000;

    public static async Task<ProcessOutcome> RunAsync(string fileName, IEnumerable<string> args, string workingDirectory,
        int timeoutSeconds, int maxOutputChars, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args)
            psi.ArgumentList.Add(a);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);

        var sw = Stopwatch.StartNew();
        process.Start();
        // Close stdin so a command that waits for keyboard input gets end-of-input instead of hanging.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
                throw;
            timedOut = true;
        }
        // Let the async readers drain the last lines.
        using (var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            try { await process.WaitForExitAsync(drain.Token); } catch (OperationCanceledException) { }
        }
        sw.Stop();

        string outText, errText;
        lock (stdout) outText = stdout.ToString();
        lock (stderr) errText = stderr.ToString();

        bool truncated = outText.Length + errText.Length > maxOutputChars;
        string? fullFile = null;
        if (truncated)
        {
            fullFile = Path.Combine(AppPaths.SubDir("outputs"), $"output-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            await File.WriteAllTextAsync(fullFile, $"--- stdout ---\n{outText}\n--- stderr ---\n{errText}\n", CancellationToken.None);
            int half = maxOutputChars / 2;
            outText = Cut(outText, half);
            errText = Cut(errText, maxOutputChars - Math.Min(outText.Length, half));
        }

        return new ProcessOutcome(
            process.HasExited && !timedOut ? process.ExitCode : null,
            outText, errText, timedOut, truncated, fullFile, Math.Round(sw.Elapsed.TotalSeconds, 2));
    }

    static void Append(StringBuilder sb, string? line)
    {
        if (line is null) return;
        lock (sb)
        {
            if (sb.Length < HardCapChars)
                sb.Append(line).Append('\n');
        }
    }

    static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Keeps the start and the end (errors are often at the end) and marks the gap.</summary>
    public static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        int head = max / 2, tail = max - head;
        return text[..head] + $"\n[... {text.Length - max} characters cut ...]\n" + text[^tail..];
    }
}
