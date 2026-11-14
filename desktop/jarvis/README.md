# JARVIS Desktop

A personal assistant for Windows 10/11 that lives in the system tray. It keeps reminders and tasks, remembers
what you tell it, opens apps and workspaces, searches your files, watches system health, runs multi-step jobs in the
background, and talks to Google Gemini when a request needs a language model.

Everything under `desktop/jarvis/` is self-contained. It shares no code, build files or workflows with the Android
app elsewhere in this repository.

> **Build status: not yet compiled.** This code was written in an environment with no .NET SDK and no Windows
> machine. It has been checked only with the static checkers in `tools/` (see [JARVIS_PROGRESS.md](JARVIS_PROGRESS.md)).
> The first real build happens when the CI workflow is enabled (see [Enabling CI](#enabling-ci)). Expect compiler
> errors to fix on that first run.

## What it does

| Area | Examples |
| --- | --- |
| Reminders and tasks | "remind me to call mom tomorrow at 7 pm", "remind me every weekday at 9 am to check mail", "add an urgent task to fix the login bug", "plan my day" |
| Memory | "remember that my thesis deadline is 30 November", "what do you remember about me". Passwords, PINs and keys are refused. |
| Modes | Personal, Study, Development, Research, Focus, System. Each has its own behaviour, tool set and optional workspace (apps, folders, files, project). |
| Apps and system | Open, close and switch apps; volume, mute, brightness; CPU, memory, disk and battery status; open Windows settings pages; lock the PC. |
| Files | Find, list, read, rename, copy, move; find duplicates and old files; organise a folder with a preview and undo. |
| Search | Local full-text index (SQLite FTS5) over folders you choose, including PDF and Word (.docx) text. |
| Development | Open the project in the IDE or a terminal, Git status/log/branches, run commands you have approved in advance. |
| Focus | Timed focus sessions that can close the distracting apps you listed and reopen them afterwards. |
| Background tasks | Built-in workflows that work offline, and Gemini-planned tasks. Pause, resume, cancel; survives a restart. |
| Screen and clipboard | Read only when your message explicitly asks, and noted in the chat each time. |
| Voice | Hold-to-talk dictation and spoken replies using the speech engines built into Windows. |

Without a Gemini API key, or with no network, JARVIS still handles the local commands above and says plainly when a
request needs Gemini. It does not invent an answer.

## Install

Download `JARVIS-Setup-<version>.exe` from the workflow run's artifacts and run it. It installs for the current user
only (no administrator prompt) into `%LOCALAPPDATA%\Programs\JARVIS`. The build is self-contained: no .NET runtime
needs to be installed.

The installer is not code-signed, so Windows SmartScreen will warn on first run ("More info" → "Run anyway").

A portable build (`JARVIS-portable-win-x64-<version>`) is also uploaded: unzip it anywhere and run `Jarvis.exe`.

## First run

1. JARVIS appears in the system tray. Press **Ctrl+Alt+J** to show or hide the assistant window.
2. Open **Settings › Gemini** and paste an API key from Google AI Studio. Save, then press *Test connection*.
3. Optional: set your name, briefing times, mode workspaces, indexed folders and distracting apps in Settings.

| Shortcut | Action |
| --- | --- |
| Ctrl+Alt+J | Show or hide the assistant window |
| Ctrl+Alt+Space (hold) | Talk; release to send |

Both shortcuts can be changed under **Settings › General**. Closing the window hides it to the tray; use the tray
menu to exit. JARVIS starts in the tray when you sign in to Windows; turn that off under **Settings › General**.

## Privacy and safety

- **Local data.** Conversations, reminders, tasks, memory, settings and the search index are stored in
  `%LOCALAPPDATA%\JARVIS\jarvis.db`. Logs are in `%LOCALAPPDATA%\JARVIS\logs` with credential-like text masked.
- **API key.** Encrypted with Windows DPAPI for your user account in `secrets.dat`. It is never written to the
  database, settings or logs.
- **What goes to Gemini.** Your message, recent turns of the conversation, relevant memory entries, a short digest of
  tasks and reminders, and the results of tools Gemini calls. Screen captures and clipboard text are sent only when
  your message asks about them.
- **Actions.** Gemini can act only through JARVIS's fixed tool list. There is no shell or script execution; the only
  commands it can run are the ones you add under **Settings › Automation**.
- **Approvals.** Every action is rated safe, routine or consequential. Consequential actions always ask first with
  a preview: overwriting a file, sorting a folder or undoing that, running a command you have not marked as trusted,
  re-running an interrupted task step, and reading the screen or clipboard when your message did not ask for it.
  Routine actions (opening and closing apps, renaming or moving a file, changing volume) ask unless *Trust routine
  actions* is on, which it is by default. Any tool can be set to always ask or never run under
  **Settings › Permissions**.
- **Files.** Access is limited to your user folders plus folders you add. Windows, Program Files and `AppData` are
  off limits.
- **Audit.** Every tool call and its outcome is listed under **Settings › Activity & data**, where you can also
  delete conversations, memory and the search index.

## Build from source

Requirements: Windows 10/11 x64, .NET 10 SDK. Inno Setup 6.3+ only for the installer.

```powershell
cd desktop\jarvis
dotnet restore Project-JARVIS.sln
dotnet build Project-JARVIS.sln -c Release
dotnet test Project-JARVIS.sln -c Release

dotnet publish src\Jarvis.Desktop\Jarvis.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish
publish\Jarvis.exe

# Installer
iscc installer\Jarvis.iss /DAppVersion=1.0.0 /DSourceDir=..\publish
```

Useful switches:

| Switch / variable | Effect |
| --- | --- |
| `Jarvis.exe --tray` | Start hidden in the tray (used by the Windows start-up entry) |
| `Jarvis.exe --smoke-test <file>` | Start against a throw-away data folder, exercise the UI and local commands, write the result to `<file>`, exit 0 on success |
| `JARVIS_DATA_DIR` | Use a different data folder |

## Enabling CI

The workflow is stored at `ci/jarvis-windows-build.yml` rather than in `.github/workflows/`, because the session
that wrote it was not permitted to push workflow files. To turn it on:

```powershell
copy desktop\jarvis\ci\jarvis-windows-build.yml .github\workflows\jarvis-windows-build.yml
git add .github\workflows\jarvis-windows-build.yml
git commit -m "Enable JARVIS Desktop workflow"
git push
```

It runs on changes under `desktop/jarvis/` and on manual dispatch. It restores, builds, runs the tests, publishes,
runs the smoke test, builds the installer, installs and uninstalls it silently, and uploads the installer, the
portable build and the test results. The Android workflow is not touched.

## Layout

```
desktop/jarvis/
  Project-JARVIS.sln
  src/
    Jarvis.Core            models, contracts, tool schemas, security policy, schedule parsing (no dependencies on Windows)
    Jarvis.Infrastructure  SQLite stores, full-text index, text extraction, file logging
    Jarvis.AI              Gemini client, prompt and history building, conversation summaries
    Jarvis.Agent           tools, tool executor, modes, task engine, scheduler, offline interpreter, assistant loop
    Jarvis.Windows         Win32/WMI/DPAPI/speech/tray/hotkey implementations
    Jarvis.Desktop         WPF application (Jarvis.exe)
  tests/Jarvis.Tests       xUnit tests
  installer/Jarvis.iss     Inno Setup script
  ci/                      GitHub Actions workflow (to be copied into .github/workflows)
  tools/                   static checkers used while writing the code without a compiler
  docs/ARCHITECTURE.md
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the pieces fit together and
[JARVIS_PROGRESS.md](JARVIS_PROGRESS.md) for what has and has not been verified.
