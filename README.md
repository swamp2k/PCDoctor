# PC Doctor

A desktop app for the family's Windows PCs. You describe a problem in plain words
("Why does Minecraft crash?", "Is my internet down?") and an AI agent (Claude Haiku 5.5)
investigates it itself with built-in diagnostic tools and, if you allow it, PowerShell.

No login, no accounts, no cloud backend. The only thing it talks to is the Anthropic API.

## Getting the app

Every push builds `PCDoctor.exe` on a Windows machine in GitHub Actions:
open the repository's **Actions** tab, pick the latest green "Build and test (Windows)" run,
and download the **PCDoctor-win-x64** artifact. It is a single self-contained `.exe`;
nothing else needs to be installed.

## First start

1. Start `PCDoctor.exe`. Windows SmartScreen may warn because the file is not signed;
   choose "More info" and "Run anyway".
2. Click **Indstillinger**, paste your Anthropic API key, click **Gem nøgle**, then **Test forbindelse**.
   The key is saved in Windows Credential Manager for your Windows user and reused automatically.
   It is never stored in the program file or a settings file.
3. Type a question and press Enter.

## The three modes

| Mode | What the agent may do |
|---|---|
| **Read-only** (default at every start) | Only built-in tools that read information. No free PowerShell at all. |
| **Assisted** | Built-in tools freely. Every PowerShell command is shown in full (with its folder) and runs only if you click "Kør kommandoen". |
| **YOLO** | PowerShell without asking, with your normal Windows rights. Never as administrator. Not risk-free. |

**Stop** ends the investigation and kills any running command. Each command also has a time limit.

## Recording performance

**Optag ydelse** opens the recorder. Press Start, play the game (or do whatever is slow), come back and press Stop.
It stops by itself after 15 minutes. About once a second it notes CPU, graphics card (GPU), RAM and disk use, and which
program used the most CPU. The screen is not recorded. Afterwards you see a chart and a summary, and
**Spørg PC Doctor om optagelsen** asks the agent what held the PC back. The newest 20 recordings are kept.

## Built-in tools

| Tool | What it does |
|---|---|
| `system_hardware` | CPU, graphics cards and drivers, RAM, Windows version, disks, motherboard (WMI, no shell) |
| `system_processes` | Top programs by CPU and memory, measured over one second |
| `network_ping` | Ping the router, 1.1.1.1 and 8.8.8.8, or named hosts |
| `network_dns` | Resolve names and list the DNS servers in use |
| `network_tcp` | Can we connect to host:port? |
| `performance_recording` | Reads a recording made with Optag ydelse: summary, timeline and top programs |
| `shell_execute` | Windows PowerShell, governed by the mode above |

## Where things are saved

Everything stays on the PC, in `%LOCALAPPDATA%\PCDoctor`:

- `sessions\` one log file per conversation: questions, tool calls, commands, output and answers.
- `outputs\` full output of commands whose output was too long to send to the model.
- `recordings\` performance recordings (newest 20).
- `settings.json` limits (rounds per question, time limits, output size). No secrets.

## For developers

```
src/PCDoctor.Core              contracts: tools, modes, settings, process runner
src/PCDoctor.Agent             agent loop, permission check, Anthropic API adapter
src/PCDoctor.Diagnostics       cross-platform tools (processes, ping, DNS, TCP)
src/PCDoctor.Platform.Windows  WMI hardware, PowerShell, Credential Manager
src/PCDoctor.App               Avalonia window
tests/PCDoctor.Tests           xUnit tests
```

Build and test: `dotnet test` (needs the .NET 10 SDK). Windows-only tests are skipped on other systems.
Every model tool call goes through `ToolInvoker`, the single place where the mode is checked;
nothing else starts a process for the model.
