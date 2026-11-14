# JARVIS Desktop (Windows)

A lightweight, voice-first personal AI assistant for Windows 11. See
`../JARVIS_PROGRESS.md` for the full phase-by-phase status and
`docs/ARCHITECTURE.md` for design details.

## Quick start (Windows)
1. Install .NET 10 SDK (https://dotnet.microsoft.com/download) — or just run the
   installer from the GitHub Actions artifacts (self-contained, no runtime needed).
2. `dotnet build desktop/Jarvis.sln -c Release`
3. `dotnet run --project desktop/src/Jarvis.Desktop -c Release`
4. Open Settings, paste your Gemini API key (https://aistudio.google.com/apikey),
   choose a model, and press Talk or type.

## Defaults
- Push-to-talk / show window: `Ctrl+Alt+J` (changeable in Settings)
- Starts with Windows into the system tray (per-user, no admin)
- Closing the window hides to tray; use tray → Exit to quit fully
- No API key required to install or launch — local mode works offline

## Tests
`dotnet test desktop/Jarvis.sln -c Release`

## Installer / portable build
GitHub Actions (`.github/workflows/jarvis-desktop.yml`) builds, tests, publishes
a self-contained win-x64 app, and produces `JARVIS-Setup.exe` (Inno Setup,
per-user) + `JARVIS-Portable-win-x64.zip` as artifacts.
