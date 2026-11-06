# Instructions for AI coding agents (PC Doctor)

AGENTS.md and CLAUDE.md in the repository root must always have identical contents.

## Every session

- At the start of every session, read `NOW.md` in the repository root. It says what is being worked on, the current state and the next step.
- Before ending every session, update `NOW.md` (fields: Doing, State, Next step, Blocked by, Updated as YYYY-MM-DD). Base it on what you verified; mark anything uncertain.
- Whenever either root instruction file (AGENTS.md or CLAUDE.md) changes, update both identically.
- Leave nested instruction files (in subfolders) unchanged unless asked.

## The owner

- The owner is not a programmer. Explain results in plain, simple language and keep code and architecture simple.

## How changes are made

- Never push straight to `main`. Work on a branch and open a pull request.
- Every push and pull request runs `.github/workflows/build.yml` (tests and builds on Windows and Linux).
- Every merge to `main` publishes a new GitHub Release automatically (version 1.0.<commit count>), and the app offers it through its "Update now" button. PC Doctor is not rolled out to the family's PCs by that: only PCWatch is.

## Code facts worth knowing

- .NET 10, Avalonia UI. Project layout is described in README.md under "For developers".
- Build and test: `dotnet test` (needs the .NET 10 SDK). Windows-only tests are skipped on other systems.
- Every model tool call goes through `ToolInvoker`, the single place where the mode (Read-only, Assisted, YOLO) is checked. Nothing else may start a process for the model.
- The API key is never stored in code or settings files (Windows Credential Manager, or `~/.local/share/PCDoctor/api-key` on Linux).
- The app is translated into Danish (default), English, Swedish, Norwegian and German; new user-facing text needs all five.
