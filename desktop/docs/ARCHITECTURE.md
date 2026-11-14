# JARVIS Desktop — Architecture

## Stack
- Windows 11 x64, .NET 10 LTS, WPF (C#), MVVM + Microsoft.Extensions.DependencyInjection
- SQLite (Microsoft.Data.Sqlite), Windows DPAPI for the Gemini key
- Gemini via REST `generateContent` (v1beta) with function calling — see
  https://ai.google.dev/gemini-api/docs/get-started and
  https://ai.google.dev/gemini-api/docs/function-calling
- Default model: `gemini-2.5-flash` (configurable in Settings; never hardcoded elsewhere)

## Layers (desktop/src)
| Project | Responsibility |
|---|---|
| Jarvis.Core | Models, settings, memory, tasks, modes, tool registry + permissions, file search, reminder scheduler. No UI, no Windows APIs. |
| Jarvis.AI | GeminiClient (REST, timeouts, function-call parsing, image turns), ConversationService (memory → Gemini → tools → reply, offline fallback with LOCAL provenance). |
| Jarvis.Infrastructure | SQLite stores (conversations/messages/memories/tasks/reminders), DPAPI vault. |
| Jarvis.Windows | App/window/folder control, system info, volume (Core Audio), on-demand screenshot + clipboard, Windows Speech STT/TTS, tray/toast notifications, HKCU startup, global hotkey. |
| Jarvis.Agent | TaskEngine (background multi-step execution, pause/resume/cancel, restart recovery), WorkflowPlanner (dev-environment setup, file-organisation previews), safe file ops. |
| Jarvis.Desktop | WPF floating window, settings window, view models, DI composition, single-instance + tray lifecycle. |

## Tool & permission architecture
Gemini never executes OS commands. It returns a structured function call;
`ToolRegistry.ExecuteAsync` validates the name and runs the local handler.
Risk tiers: `Trusted` (runs without confirmation only if the user listed it in
Settings → Trusted actions), `ApprovalRequired` (always confirm), `Destructive`
(confirm + preview; file moves never overwrite). There is deliberately no
shell/PowerShell tool. Modes further restrict which tools are available.

## Privacy boundaries
- Microphone: only inside `ListenOnceAsync` after push-to-talk. No background listening.
- Screen/clipboard: only on explicit request (`capture_screen`, `read_clipboard_summary`).
- API key: DPAPI CurrentUser file under `%LocalAppData%\JARVIS\gemini.key`; never in
  settings JSON, logs, or artifacts.
- Memory: secrets are rejected by `MemoryService`; memories are user-reviewable.
- Offline: no key/network → local memory, reminders, tasks, search and Windows
  tools still work; replies are labelled LOCAL, never presented as Gemini.

## Data
`%LocalAppData%\JARVIS\` — `settings.json`, `jarvis.db` (SQLite), `gemini.key` (DPAPI),
`captures\` (on-demand screenshots). Uninstall keeps data; delete the folder to wipe.
