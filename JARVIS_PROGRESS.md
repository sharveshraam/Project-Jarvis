# JARVIS Progress

## Branch & layout
- Work is on branch `arena/98f80e17-project-jarvis` (Arena fixes the session branch
  name; rename to `Jarvis-pc` / `feature/jarvis-desktop` on GitHub after push —
  the PR follows the rename). The requested `feature/jarvis-desktop` name cannot
  be created from this session.
- Existing Android app (root `app/`, `core/`, Gradle, `android.yml`) is preserved
  untouched. The Windows desktop app is isolated under `desktop/` so the two
  never share dependencies or build files.

## Phase 1 — Foundation ✅ implemented
- WPF floating assistant (identity/status, conversation, text input, push-to-talk,
  mic indicator, mode selector, task status, connection indicator, settings/history)
- System tray (Open / Settings / Exit), HKCU Windows startup, global hotkey
  `Ctrl+Alt+J`, single-instance mutex, structured logging (Microsoft.Extensions.Logging)
- Gemini REST client with timeouts, retries via caller, function-calling loop,
  image turns for screenshot analysis; DPAPI key storage; no key needed to launch
- Push-to-talk STT + TTS via Windows Speech, interrupt button, text fallback
- SQLite conversation storage: view/resume/new/delete/clear
- Offline: launch, tray, settings, memory, reminders, tasks, Windows tools, file
  search all work; replies labelled LOCAL

## Phase 2 — Windows & computer intelligence ✅ implemented
- App control: launch (known-app map + paths), graceful close (never force-kill),
  switch window, running-process detection, open folders/files
- System: volume/mute (Core Audio), CPU/memory/disk/process info on demand;
  battery honestly reported as unavailable where the API cannot provide it
- Screen: explicit one-shot capture → PNG → Gemini image analysis; no recording
- Files: open/find/create-folder/rename/move/copy/inspect via tools + search;
  destructive changes previewed and approval-gated; duplicates by SHA-256
- Clipboard: one-shot read only on explicit request
- Universal search: file names + text content over user-selected folders
  (txt/md/code/etc.), with exclusions and excerpts; local only
- App-specific: VS Code, Windows Terminal, browsers, Git-adjacent folders via
  launch/open tools; unsupported operations reported, not faked

## Phase 3 — Personal intelligence & modes ✅ implemented
- Long-term memory (SQLite): view/edit/delete/clear, explicit vs inferred,
  secret rejection
- Conversation continuity: history, resume, summaries column, context window
  capped (`MaxConversationTurnsSentToGemini`)
- Secretary: NL reminders (one-time/daily/weekly), deadlines via due dates,
  priorities via task ordering, overdue detection, Windows notifications,
  restart recovery for reminders that became due while closed
- Six modes (Study/Development/Research/Focus/System/Personal) with distinct
  allowed-tool sets, persisted active mode, UI + voice switching

## Phase 4 — Automation & background agent ✅ implemented
- TaskEngine: Pending/Running/WaitingForApproval/Paused/Completed/Failed/Cancelled,
  per-step verification, background execution, progress, pause/resume/cancel,
  restart recovery (Running → Paused, never auto-repeated)
- Workflows: dev-environment setup plan; file-organisation preview → approved moves
- Trusted routine actions configurable in Settings; destructive actions gated;
  no unrestricted shell access

## Phase 5 — Integration, reliability, release ✅ implemented (CI-verified)
- All subsystems wired through DI in `App.xaml.cs`; UI never blocks on network
  (async + cancellation tokens throughout)
- Tests: Core (registry/permissions, memory, modes, reminders, tasks, search,
  settings), AI (Gemini parsing with stub HTTP, offline fallback), Agent
  (organisation preview, safe move), Windows (hotkey parsing, startup toggle)
- GitHub Actions Windows workflow: restore → build → test → publish
  self-contained win-x64 → Inno Setup installer + portable ZIP artifacts
- Privacy/security: DPAPI key, redacted logging (keys never logged), explicit
  mic/screen/clipboard, local-first storage

## Known limitations (honest)
- This sandbox is Linux without a .NET SDK, so `dotnet build/test` could not be
  executed here. Code was validated structurally (project references, XAML
  bindings, C# syntax review). The Windows CI workflow is the build/test gate —
  do not claim a green build until that run passes.
- Battery percentage: no reliable managed API; reported as unavailable.
- Brightness: hardware-dependent; not exposed as a tool rather than faked.
- DOCX/PDF text extraction: search covers plain-text formats; PDF/DOCX binary
  parsing is a documented follow-up (would add a parser dependency).
- Web research mode uses Gemini text + local search; a dedicated search API
  integration is a follow-up within the approved scope.
- No webcam/gesture/IoT features — excluded per confirmed scope.
