# NOW

**Doing:** Always-live Live View (branch `claude/project-thread-cbzfkm`, draft pull request).

**State:** (verified 2026-11-15)
- Live View now shows live readings (gauges, race, graphs, network) as soon as the page is open, about once a second, only while it is on screen. Graphs keep the last 5 minutes.
- The Start button is now "Optag" (Record) on both performance pages; it only controls saving. Recording pauses the live loop and takes over; the 15-minute cap and journal are unchanged. After a recording or when picking an old one, a "Tilbage til live" button returns to live readings.
- New text is in all 5 languages. Linux build: 221 tests pass, 8 Windows-only skipped. NOT checked on Windows on screen yet (waiting for CI and the owner).

**Next step:** Owner tries the CI exe and approves the merge.

**Blocked by:** Nothing.

**Updated:** 2026-11-15
