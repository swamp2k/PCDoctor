using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCDoctor.Core;

namespace PCDoctor.Platform.Windows;

/// <summary>
/// Reads temperature, power, clock speed and limits of an NVIDIA graphics card through NVIDIA's own library (nvml.dll,
/// installed with the driver). Nothing is started as a separate program.
/// </summary>
/// <remarks>
/// The readings are taken on a background thread, never on the recording thread: when the card or driver is
/// struggling, these calls can block for seconds, and that must not stall the recording. The time a reading took
/// is reported too, because a driver that answers slowly is itself a sign of trouble. On a PC without an NVIDIA
/// card (or without the library) <see cref="Latest"/> is always null.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class GpuHealthReader : IDisposable
{
    public sealed record Reading(double? TempC, double? PowerW, double? ClockMhz, string? Throttle, int QueryMs);

    /// <summary>A reading that has been in progress this long counts as the driver stalling.</summary>
    const int StallMs = 2000;

    readonly ManualResetEventSlim _stop = new();
    readonly IntPtr _device;
    volatile Reading? _latest;
    long _callStarted; // Stopwatch timestamp of the reading in progress, 0 when none

    public GpuHealthReader()
    {
        try
        {
            if (Nvml.Init() != 0 || Nvml.GetHandle(0, out _device) != 0)
                return;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return; // no NVIDIA driver library on this PC
        }
        new Thread(Loop) { IsBackground = true, Name = "gpu-health" }.Start();
    }

    /// <summary>The newest reading, or null if there is none. While a reading is stuck, the time it has been stuck is included.</summary>
    public Reading? Latest
    {
        get
        {
            var reading = _latest;
            long started = Interlocked.Read(ref _callStarted);
            int stuckMs = started == 0 ? 0 : (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (stuckMs < StallMs)
                return reading;
            return (reading ?? new Reading(null, null, null, null, 0)) with { QueryMs = Math.Max(reading?.QueryMs ?? 0, stuckMs) };
        }
    }

    void Loop()
    {
        while (!_stop.IsSet)
        {
            long started = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _callStarted, started);
            try
            {
                double? temp = Nvml.GetTemperature(_device, 0, out uint t) == 0 ? t : null;
                double? power = Nvml.GetPowerUsage(_device, out uint mw) == 0 ? Math.Round(mw / 1000.0, 1) : null;
                double? clock = Nvml.GetClockInfo(_device, 0, out uint mhz) == 0 ? mhz : null;
                string? throttle = Nvml.GetThrottleReasons(_device, out ulong mask) == 0 ? GpuThrottle.Decode(mask) : null;
                _latest = new Reading(temp, power, clock, throttle, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or SEHException)
            {
                return;
            }
            finally
            {
                Interlocked.Exchange(ref _callStarted, 0);
            }
            _stop.Wait(2000);
        }
    }

    /// <summary>Stops the reading thread. It does not wait for it: a stuck driver call must not hold up closing the app.</summary>
    public void Dispose() => _stop.Set();

    static class Nvml
    {
        const string Lib = "nvml.dll";

        [DllImport(Lib, EntryPoint = "nvmlInit_v2"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int Init();

        [DllImport(Lib, EntryPoint = "nvmlDeviceGetHandleByIndex_v2"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int GetHandle(uint index, out IntPtr device);

        [DllImport(Lib, EntryPoint = "nvmlDeviceGetTemperature"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int GetTemperature(IntPtr device, int sensor, out uint celsius);

        [DllImport(Lib, EntryPoint = "nvmlDeviceGetPowerUsage"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int GetPowerUsage(IntPtr device, out uint milliwatts);

        [DllImport(Lib, EntryPoint = "nvmlDeviceGetClockInfo"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int GetClockInfo(IntPtr device, int clockType, out uint mhz);

        [DllImport(Lib, EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int GetThrottleReasons(IntPtr device, out ulong reasons);
    }
}
