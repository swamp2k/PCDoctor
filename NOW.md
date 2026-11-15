# NOW

**Doing:** Export button on the Live View / recording page (branch `claude/project-thread-cbzfkm`, draft pull request).

**State:** (verified 2026-11-15)
- Always-live Live View (PR #25) is merged.
- New: an "Eksportér" (Export) button left of "Ask PC Doctor about the recording". It saves the recording on screen as a CSV file (one row per second, all measurements), using this PC's list separator and decimal mark so Excel opens it correctly; UTF-8 with byte order mark. Translated into all 5 languages. A test covers the CSV format.
- Linux build: 222 tests pass, 8 Windows-only skipped. NOT seen on screen on Windows yet (waiting for CI and the owner).

**Next step:** Owner tries the CI exe (export a recording, open it in Excel) and approves the merge.

**Blocked by:** Nothing.

**Updated:** 2026-11-15
