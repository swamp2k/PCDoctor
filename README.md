# PC Doctor

A simple app that helps the family fix PC problems. You describe the problem in plain words
("Why does Minecraft crash?", "Is my internet down?") and an AI helper (Claude Haiku 5.5)
investigates it itself with built-in checks and, if you allow it, commands on the PC.

It runs on Windows and on Linux, including the Steam Deck.
No login, no accounts, no cloud server. The only thing it talks to is the Anthropic API.

**Languages:** the app itself is available in Danish, English, Swedish, Norwegian and German.
Danish is the default; change it with the flag button in the top bar.

## Getting the app

Download the newest version from the repository's **Releases** page (right side of the GitHub page):

- **Windows:** `PCDoctor.exe`
- **Linux / Steam Deck:** `PCDoctor-linux-x64`

Each is a single file; nothing else needs to be installed. A new release appears automatically every time a change is merged.

### Keeping it up to date

When a newer version exists, the app shows an **Update now** button. Click it and PC Doctor downloads the new version,
replaces itself and restarts. You do not have to download it by hand again.

## First start

### Windows

1. Start `PCDoctor.exe`. Windows SmartScreen may warn because the file is not signed;
   choose "More info" and "Run anyway".
2. Open **Settings**, paste your Anthropic API key, save it, then test the connection.
   The key is saved in Windows Credential Manager for your Windows user, never in the program or a settings file.
3. Type a question and press Enter.

### Steam Deck (and other Linux PCs)

1. Switch to **Desktop Mode** (Steam button, Power, Switch to Desktop).
2. Download `PCDoctor-linux-x64` from the Releases page, for example into Downloads.
3. Make the file runnable: right-click it in the file manager (Dolphin), choose **Properties**, open **Permissions**,
   tick **Is executable** and click OK. (In a terminal: `chmod +x PCDoctor-linux-x64`.)
4. Double-click the file to start it.
5. Open **Settings** and add your API key as on Windows. On Linux it is kept in a file only your user can read
   (`~/.local/share/PCDoctor/api-key`).

Measuring FPS is Windows-only; everything else works on Linux too.

## The three modes

| Mode | What PC Doctor may do |
|---|---|
| **Read-only** (default at every start) | Only built-in checks that read information. It cannot run its own commands. |
| **Assisted** | Built-in checks freely. Every command is shown in full first and only runs if you approve it. |
| **YOLO** | Runs commands without asking, with your normal user rights. Never as administrator. Not risk-free. |

**Stop** ends the investigation and kills any running command. Each command also has a time limit.

## What the app can do

### Ask PC Doctor

The main window is a chat. Ask about anything on the PC and PC Doctor looks into it and answers in your language.
It only talks about PC topics.

### Diagnostics (no AI needed)

**Diagnostics** shows the same checks the AI uses, directly, with no API key and no AI call. The tabs cover hardware,
usage, network, the event log, game logs and whether a game service (Fortnite, Discord, Steam...) is down.
**Overview** sums up everything that looks red or yellow. **Ask PC Doctor** hands a found problem to the chat.
Nothing on the PC is changed.

### Recent games

**Recent games** lists up to 20 games that wrote log files lately, including Steam, Epic, Xbox app and Minecraft games
(and Steam/Proton games on Linux). Pick one, click **Investigate** and choose what is wrong:
it crashes, it stutters, it lags, or it cannot go online.

### Record performance

Press Start, play the game (or do whatever is slow), come back and press Stop. It stops by itself after 15 minutes.
About once a second it notes CPU, graphics card, RAM and disk use, the busiest programs, the processor's speed,
graphics memory and a ping to the internet to catch lag spikes. The screen is not recorded.
Afterwards you see a chart and a summary, and can ask PC Doctor what held the PC back. The newest 20 recordings are kept.

On Windows you can also measure FPS (frames per second). Windows only allows this with administrator rights, so it asks
"Do you want to allow this app to make changes?". Only a small, windowless helper that counts frames runs as administrator;
the main window never does. If you say no, everything else is still recorded.

### Live View

Shows all running programs and a live "race" between CPU, graphics card, RAM, disk and network. The bar in front is the
likely reason a game stutters. A network card shows ping and speed, and you can switch to classic graphs.

### Log Review

Under Record performance. Open a game's log file and PC Doctor finds the game and the error lines and explains them.
Only the error lines are sent (or the whole log, up to 600 KB, if you choose).

### History

**History** lists earlier conversations, newest first: the questions and answers, every check PC Doctor ran, and,
for troubleshooting, exactly what was sent to the AI. The API key and anything that looks like a password is hidden
before anything is saved. The newest 100 conversations are kept, and you can delete any of them.

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
| `performance_recording` | Reads a recording made with Record performance: summary, timeline and top programs |
| `shell_execute` | Windows PowerShell (bash on Linux), governed by the mode above |

## Where things are saved

Everything stays on the PC, in `%LOCALAPPDATA%\PCDoctor` on Windows and `~/.local/share/PCDoctor` on Linux:

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
                               event grouping, plain-language reports for the Diagnostics page)
src/PCDoctor.Platform.Windows  WMI hardware, Event Log, performance counters, PowerShell, Credential Manager
src/PCDoctor.Platform.Linux    Linux hardware, system journal, bash, API key file
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
