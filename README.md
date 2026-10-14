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
It stops by itself after 15 minutes. About once a second it notes CPU, graphics card (GPU), RAM and disk use, which
programs used the CPU and the graphics card most, the processor's speed (it slows down when it gets too hot or saves power),
how much graphics memory (VRAM) is in use, and a ping to the internet (1.1.1.1) to catch lag spikes. The screen is not recorded. Afterwards you see a chart and a summary, and
**Spørg PC Doctor om optagelsen** asks the agent what held the PC back. The newest 20 recordings are kept.

## Diagnostics without AI

**Diagnostik** opens a page with seven tabs: Hardware, Forbrug (usage), Netværk, Hændelser (event log), Minecraft
(crash reports and a mod check), Andre spil (logs from Fortnite, Roblox and other games) and Spiltjenester
(is a game's service down, or is it our internet?).
Each tab runs the same built-in tools the agent uses, directly, and shows the result in plain Danish.
It needs no API key, makes no AI call, and changes nothing on the PC (Spiltjenester reads public status pages on the internet). "Vis rådata" shows exactly what the agent
would see, and **Spørg PC Doctor om dette** hands the topic to the agent in the main window.

## History

**Historik** lists earlier conversations, newest first. Pick one to see the questions and answers (**Samtale**),
every check PC Doctor ran with its input and result (**Aktivitet**), and, for troubleshooting, exactly what was sent
to the AI and what came back (**API-forespørgsler**). **Om samtalen** shows the model, language, mode, settings and
a short description of the PC. The API key and anything that looks like a password or key are replaced by `[skjult]`
before anything is saved. The newest 100 conversations are kept; **Slet samtalen** deletes one.

## Built-in tools

| Tool | What it does |
|---|---|
| `system_hardware` | CPU, graphics cards and drivers, RAM, Windows version, disks, motherboard, devices with driver problems (WMI, no shell) |
| `system_resources` | How busy the PC is right now: CPU, GPU, RAM, disk activity, free space per drive, time since restart |
| `system_processes` | Top programs by CPU and memory, measured over one second |
| `system_events` | Errors from the Windows Event Log, grouped and explained (unexpected restarts, blue screens, crashes, driver resets, disk errors); can look around a given time |
| `network_ping` | Ping the router, 1.1.1.1 and 8.8.8.8, or named hosts |
| `network_dns` | Resolve names and list the DNS servers in use |
| `network_tcp` | Can we connect to host:port? |
| `gaming_logs` | Finds Minecraft (official launcher, CurseForge, Prism, Modrinth, ATLauncher, GDLauncher) and reads crash reports, Java crash logs and latest.log |
| `minecraft_mods` | Reads every mod in a Minecraft install (Fabric, Quilt, Forge, NeoForge): loader, versions, and mods that are missing, doubled, damaged or made for another loader |
| `other_game_logs` | Logs of Fortnite, Rocket League, Fall Guys and other Unreal Engine games, Roblox and Unity games, with warning signs (graphics driver crash, out of memory, anti-cheat, lost connection) |
| `service_status` | Is Fortnite, Epic, Discord, Roblox, Minecraft, Steam or Xbox down? Reads the official status page where there is one (Epic, Discord) and tests whether this PC reaches the servers and a neutral site |
| `game_requirements` | Can this PC run a game? Requirements built in for Minecraft, Fortnite and Roblox (copied from the publishers 2026-10-14), any other game from the Steam store; compares RAM and disk space |
| `performance_recording` | Reads a recording made with Optag ydelse: summary, timeline and top programs |
| `shell_execute` | Windows PowerShell, governed by the mode above |

## Where things are saved

Everything stays on the PC, in `%LOCALAPPDATA%\PCDoctor`:

- `history.db` the conversation history (SQLite): questions, tool calls, commands, results, answers, approvals
  and the API requests and responses, with secrets masked. Newest 100 conversations.
- `outputs\` full output of commands whose output was too long to send to the model.
- `recordings\` performance recordings (newest 20).
- `settings.json` limits (rounds per question, time limits, output size). No secrets.

## For developers

```
src/PCDoctor.Core              contracts: tools, modes, settings, process runner
src/PCDoctor.Agent             agent loop, permission check, Anthropic API adapter
src/PCDoctor.Diagnostics       cross-platform tools and analysis (processes, resources, network, Minecraft logs,
                               event grouping, plain-Danish reports for the Diagnostics page)
src/PCDoctor.Platform.Windows  WMI hardware, Event Log, performance counters, PowerShell, Credential Manager
src/PCDoctor.Storage           SQLite conversation history
src/PCDoctor.App               Avalonia window
tests/PCDoctor.Tests           xUnit tests
```

Build and test: `dotnet test` (needs the .NET 10 SDK). Windows-only tests are skipped on other systems.
Crash-log fixtures with their known outcomes are listed in `tests/PCDoctor.Tests/Fixtures/known-outcomes.json`;
add a file and a line there to add a regression case. One test runs the real Claude Haiku 5.5 against a crash fixture;
it is skipped unless `PCDOCTOR_LIVE_TEST_KEY` holds an API key.
Every model tool call goes through `ToolInvoker`, the single place where the mode is checked;
nothing else starts a process for the model.
