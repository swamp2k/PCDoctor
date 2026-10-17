using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using PCDoctor.Core;

namespace PCDoctor.Platform.Windows;

/// <summary>
/// Measures a game's FPS the way PresentMon does: Windows logs an event (ETW) every time a program hands a finished frame
/// to the screen, and we count the chosen game's events. Windows only shares these events with administrators, so this runs
/// in a separate copy of PCDoctor.exe that the user starts with administrator rights (Windows asks "Do you want to allow…").
/// The main app never runs as administrator.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FpsCapture
{
    public const string HelperArg = "--fps-capture";

    /// <summary>Microsoft-Windows-DXGI: Present_Start (id 42) for every frame of a DirectX game.</summary>
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    const int DxgiPresentStart = 42;

    /// <summary>Microsoft-Windows-DxgKrnl: Present_Info (id 184), also for OpenGL and Vulkan games such as Minecraft Java.</summary>
    static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    const int KernelPresentInfo = 184;
    const ulong KernelPresentKeyword = 0x8000000;

    const string SessionName = "PCDoctor-FPS";
    static readonly TimeSpan MaxDuration = PerfRecorder.MaxDuration + TimeSpan.FromMinutes(1);

    /// <summary>A running measurement as the main app sees it.</summary>
    public sealed class Handle(string outFile, string stopFile)
    {
        public string OutFile { get; } = outFile;

        /// <summary>Asks the helper to stop, waits for its result and lines it up with the recording.</summary>
        public async Task<FpsResult> StopAsync(DateTimeOffset recordingStarted)
        {
            try { File.WriteAllText(stopFile, "stop"); } catch (IOException) { }
            for (int i = 0; i < 40 && !File.Exists(OutFile); i++)
                await Task.Delay(250);
            var file = FpsCaptureFile.Load(OutFile);
            try { File.Delete(OutFile); File.Delete(stopFile); } catch (IOException) { }
            return file?.ToResult(recordingStarted) ?? FpsResult.NotMeasured("The FPS measurement did not finish (it was closed or blocked).");
        }
    }

    /// <summary>
    /// Starts the helper with administrator rights for the game with this process name. Windows shows its permission prompt.
    /// Returns null and a reason in plain Danish if the user said no or it could not start.
    /// </summary>
    public static (Handle? Handle, string? Problem) Start(string gameProcess, string folder)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (null, L.T("FPS kan kun måles fra PCDoctor.exe.", "FPS can only be measured from PCDoctor.exe.", "FPS kan bara mätas från PCDoctor.exe.", "FPS kan bare måles fra PCDoctor.exe.", "FPS kann nur aus PCDoctor.exe gemessen werden."));
        Directory.CreateDirectory(folder);
        string id = Guid.NewGuid().ToString("N")[..8];
        string outFile = Path.Combine(folder, $"fps-{id}.json");
        string stopFile = Path.Combine(folder, $"fps-{id}.stop");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas", // ask Windows for administrator rights for this one helper
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string a in new[] { HelperArg, gameProcess, outFile, stopFile, Environment.ProcessId.ToString() })
            psi.ArgumentList.Add(a);
        try
        {
            Process.Start(psi)?.Dispose();
            return (new Handle(outFile, stopFile), null);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: the user clicked No
        {
            return (null, L.T("Du sagde nej til administrator-tilladelsen, så FPS måles ikke. Resten bliver stadig optaget.",
                "You said no to administrator permission, so FPS is not measured. Everything else is still recorded.",
                "Du sa nej till administratörsbehörigheten, så FPS mäts inte. Resten spelas fortfarande in.",
                "Du sa nei til administratortillatelsen, så FPS måles ikke. Resten blir fortsatt tatt opp.",
                "Du hast die Administratorberechtigung abgelehnt, daher wird FPS nicht gemessen. Alles andere wird weiter aufgezeichnet."));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return (null, L.T("FPS-måleren kunne ikke starte: ", "The FPS meter could not start: ", "FPS-mätaren kunde inte starta: ", "FPS-måleren kunne ikke starte: ", "Die FPS-Messung konnte nicht starten: ") + e.Message);
        }
    }

    /// <summary>
    /// The helper itself: PCDoctor.exe --fps-capture &lt;game&gt; &lt;outFile&gt; &lt;stopFile&gt; &lt;parentPid&gt;.
    /// Records until the stop file appears, the main app exits, or 16 minutes pass, then writes the out file.
    /// </summary>
    public static int RunHelper(string[] args)
    {
        if (args.Length < 5 || args[0] != HelperArg)
            return 2;
        string game = args[1], outFile = args[2], stopFile = args[3];
        int.TryParse(args[4], out int parentPid);

        var result = new FpsCaptureFile { StartedUtc = DateTime.UtcNow };
        try
        {
            Capture(game, result, () => File.Exists(stopFile) || !Alive(parentPid), MaxDuration);
        }
        catch (Exception e)
        {
            result.Error = (TraceEventSession.IsElevated() == true ? "" : "Not running as administrator. ") + "FPS measurement failed: " + e.Message;
        }
        string tmp = outFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(result));
        File.Move(tmp, outFile, overwrite: true);
        return 0;
    }

    /// <summary>Collects the game's present times into <paramref name="result"/> until <paramref name="stop"/> says so.</summary>
    public static void Capture(string game, FpsCaptureFile result, Func<bool> stop, TimeSpan max)
    {
        // Process ids of the game, refreshed every few seconds so a restarted game is still followed.
        var pids = new HashSet<int>();
        object gate = new();
        void RefreshPids()
        {
            var now = Process.GetProcessesByName(game).Select(p => { using (p) return p.Id; }).ToHashSet();
            lock (gate) pids = now;
        }
        RefreshPids();

        using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        session.EnableProvider(Dxgi, TraceEventLevel.Informational);
        session.EnableProvider(DxgKrnl, TraceEventLevel.Informational, KernelPresentKeyword);
        DateTime started = DateTime.UtcNow;
        result.StartedUtc = started;

        session.Source.AllEvents += e =>
        {
            int id = (int)e.ID;
            bool dxgi = id == DxgiPresentStart && e.ProviderGuid == Dxgi;
            bool kernel = id == KernelPresentInfo && e.ProviderGuid == DxgKrnl;
            if (!dxgi && !kernel) return;
            lock (gate) if (!pids.Contains(e.ProcessID)) return;
            double ms = (e.TimeStamp.ToUniversalTime() - started).TotalMilliseconds;
            lock (gate) (dxgi ? result.Dxgi : result.Kernel).Add(Math.Round(ms, 3));
        };

        var watcher = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            while (!stop() && clock.Elapsed < max)
            {
                await Task.Delay(500);
                if (clock.Elapsed.TotalSeconds % 5 < 0.5) RefreshPids();
            }
            session.Stop();
        });
        session.Source.Process(); // returns when the session stops
        watcher.Wait();
    }

    static bool Alive(int pid)
    {
        if (pid <= 0) return true;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
