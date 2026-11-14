# JARVIS Desktop — architecture

## Projects and dependencies

```
Jarvis.Desktop (WPF, Jarvis.exe)
   ├── Jarvis.Windows         Win32, WMI, DPAPI, System.Speech, tray, hotkeys
   ├── Jarvis.Agent           tools, executor, task engine, scheduler, assistant loop
   │      └── Jarvis.AI       Gemini client, prompts, summaries
   ├── Jarvis.Infrastructure  SQLite, full-text index, logging
   └── Jarvis.Core            models, interfaces, schemas, policy (referenced by all of the above)
```

`Jarvis.Core` has no Windows or storage dependencies. Everything that touches the operating system sits behind an
interface declared in `Jarvis.Core/Abstractions` (`IAppController`, `IShellService`, `ISystemInfo`, `INotifier`,
`ISecretStore`, `ISpeechToText`, …) and is implemented in `Jarvis.Windows`. That is what lets the tests run the
real agent code against recording fakes.

`Jarvis.Desktop/Composition.cs` is the only place where implementations are wired to interfaces.

## A request, end to end

```
typed or dictated text
        │
        ▼
AssistantService.SendAsync ──► saves the user turn (ConversationStore)
        │
        ├─ no API key / no network ──► OfflineAssistant ──► ToolExecutor ──► tool
        │                               (regex interpreter; says so when a request needs Gemini)
        │
        └─ Gemini available:
             build system instruction (mode, time, memory, planner digest, summary)
             offer the current mode's tools
             loop (max 8 rounds):
                 GeminiClient.GenerateAsync
                 no tool calls ──► final answer
                 tool calls    ──► ToolExecutor ──► tool ──► result JSON back to Gemini
             Gemini error ──► status shown in the UI, request retried locally via OfflineAssistant
```

The mode is re-read on every round, so a `switch_mode` call changes both the instructions and the tool list of the
next round.

## Tools

A tool is a `ToolDefinition` (name, description, category, base risk, typed parameters) plus a delegate. Ten modules
under `Jarvis.Agent/Tools` declare 70 tools. Gemini receives each definition as a JSON schema; nothing else is
callable.

`ToolExecutor` is the single gate every call passes through, whether it came from Gemini, from the offline
interpreter or from a background task:

1. **Known tool?** Unknown names are rejected and audited.
2. **Allowed in this mode?** Each mode exposes a set of tool categories. Direct user commands and built-in workflows
   bypass this filter; Gemini does not.
3. **Arguments valid?** Checked against the schema (required, type, enum, range) before anything runs.
4. **Assessment.** The tool describes what this particular call would do and may raise its risk (for example a copy
   that would overwrite a file becomes consequential).
5. **Permission.** Per-tool override (always ask / never run), then risk: safe runs, routine runs if *Trust routine
   actions* is on, consequential always asks through `IApprovalService` with the assessment's preview.
6. **Run** with a timeout and cancellation. The result carries `success`, `verified` and a message.
7. **Audit.** Tool, risk, decision and outcome are recorded.

`verified` matters: a tool reports `success` with `verified=false` when it issued a request but could not confirm
the effect. The system prompt and the offline replies both pass that distinction on to the user.

## Background tasks

`TaskEngine` runs a list of steps (tool + arguments) one at a time, persisting after every step. Steps come from a
built-in workflow (`Workflows.cs`, offline) or from a Gemini plan constrained by a response schema and validated
against the tool schemas before anything runs. An invalid plan is sent back to Gemini once with the problems; a second
invalid plan fails the task.

- A failed step is retried only if the tool is marked retryable, up to three attempts.
- A step that needs approval moves the task to *waiting for approval* and notifies the user.
- Tasks can be paused, resumed and cancelled individually, and all tasks can be paused from the tray.
- On start-up `RecoverAsync` resumes unfinished tasks. If a task was interrupted in the middle of a step that is not
  safe to repeat, it first asks the tool whether the step already took effect; if that cannot be determined the
  task is held until the user chooses to re-run the step or cancel.

## Scheduler

`SchedulerService` wakes at the next due time (or every 30 seconds) and delivers reminders, the morning briefing,
the end-of-day summary and focus-session time-ups. Each is announced once; recurring reminders roll forward to their
next occurrence. Reminders that came due while JARVIS was closed are delivered late with the original time.

## Storage

One SQLite file (`jarvis.db`, WAL mode) with versioned schema in `Database.cs`: settings, conversations, messages,
memories, reminders, todos, focus sessions, agent tasks, audit, file-operation journal, indexed files and an FTS5
table for search. All SQL is parameterised.

The Gemini API key is not in the database. `DpapiSecretStore` keeps it in `secrets.dat`, encrypted for the current
Windows user.

## Conversations and memory

- The last turns of the current conversation are sent as history, within a message and character budget.
- When a conversation passes 40 turns, `ConversationSummarizer` folds the older ones into a rolling summary and only
  the 16 most recent turns are resent.
- Long-term memory is explicit: entries are created by the `remember` tool, labelled *stated* or *inferred*, and
  retrieved by keyword relevance. `SecretDetector` refuses anything that looks like a credential.

## Security boundaries

| Boundary | Enforced by |
| --- | --- |
| Only declared tools can run | `ToolRegistry`, `ToolExecutor` |
| Arguments match the schema | `SchemaValidator` |
| File access limited to user folders and added folders | `PathPolicy` |
| No arbitrary commands | `run_approved_command` runs only entries from settings, without a shell |
| Consequential actions need consent | `PermissionPolicy`, `IApprovalService` |
| Screen and clipboard only on explicit request | `PerceptionTools` assessment |
| Secrets stay out of memory, logs and the database | `SecretDetector`, `FileLogger`, `DpapiSecretStore` |

## Desktop shell

`App.xaml.cs` owns start-up: single instance (a second launch brings the first to the front), global hotkeys, tray
icon, scheduler and task recovery. `LifecycleController` decides what closing the window means (hide to tray) and
what exit means. The UI is two windows, `MainWindow` (conversation, status, approvals) and `SettingsWindow` (ten
pages), bound to view models in `ViewModels/`.

`Jarvis.exe --smoke-test <file>` builds the real object graph against a temporary data folder, validates every
locally built tool call against the schemas, shows both windows, runs a few local commands, exercises the approval
prompt, and exits with 0 only if all of that worked. CI uses it on the published build and on the installed copy.
