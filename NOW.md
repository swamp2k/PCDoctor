# NOW

**Doing:** Graphics card hang detection (branch `feat/gpu-hang-detection`, pull request open, not merged).

**State:** (verified 2026-11-06)
- Built after a real hang on the owner's PC (2026-11-06 13:12, GTX 1080, Windows "LiveKernelEvent code 141"): everything stuttered and a monitor blanked, CPU and RAM looked normal, and PC Doctor would have found nothing.
- Added: (1) `system_events` always includes graphics-driver, WHEA and live-dump entries at any level, flagged `hardware_fault` and explained in all 5 languages; (2) recordings are journalled to disk as they run (`PerfJournal`) and recovered as "interrupted" at the next start; (3) NVIDIA graphics card health in recordings (temperature, power, clock, limits, driver reply time) read through `nvml.dll` on a background thread; (4) a startup notice with an Investigate button when Windows logged such a hang since last time.
- Tests: 222 pass. 2 fail on the owner's PC only (`OtherGameLogsTests`): they read the PC's real Windows error reports, where the Xbox Game Bar shows up as a "game". They failed before this work too; not fixed.
- Also fixed: times in `system_events` came out as `13.18.22` on Danish settings, which the `around` argument cannot read back.
- Checked against the real hang on this PC: the tool returns both traces, the startup check finds one incident. NOT checked: how the startup notice looks on screen (the app starts and runs without errors, but it was not seen).
- Not done: AMD graphics cards on Windows, and the same checks on Linux / Steam Deck (no Windows error reports there; the journal tool is unchanged).
- The .NET 10 SDK is not installed globally on the owner's PC; a copy used for building is in `C:\dn10` (can be deleted).

**Next step:** Owner reviews the pull request; look at the startup notice on screen before merging.

**Blocked by:** Nothing.

**Updated:** 2026-11-06
