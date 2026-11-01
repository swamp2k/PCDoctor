# NOW

**Doing:** Nothing in progress. This session only added the AI instruction files (AGENTS.md, CLAUDE.md) and this NOW.md bookmark.

**State:** (verified 2026-11-01)
- The app works on Windows and Linux / Steam Deck. Phase 5 (Linux and Steam Deck) and an English README were the last changes, merged 2026-10-22.
- The newest release is v1.0.43 (2026-10-22). The last build of `main` passed on both Windows and Linux.
- Two old pull requests are still open and both clash with today's code (GitHub reports merge conflicts):
  - #2 "Language picker with flags; Danish by default" (2026-10-13). Most likely replaced by the later #10, which made the whole app switch language with the flag. Probably safe to close.
  - #7 "Andre spil: ask about one game instead of all logs" (2026-10-14). Not in `main` (the `game` option it adds does not exist there). Uncertain whether the later "Recent games" page (#16, #17) already covers the same need.
- 18 old `claude/...` branches remain on GitHub from merged or abandoned work.

**Next step:** Owner decides what to do with PR #2 and #7 (close, or redo #7 on top of today's code). After that, pick the next feature.

**Blocked by:** Owner's decision on the two old pull requests.

**Updated:** 2026-11-01
