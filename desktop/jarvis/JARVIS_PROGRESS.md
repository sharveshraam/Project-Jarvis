# JARVIS Desktop — progress and verification status

Last updated: 2026-11-14

## Summary

The application, its tests, the installer script and the CI workflow are written. **None of it has been compiled or
run.** The environment it was written in had no .NET SDK and no Windows machine, and the GitHub token could not push
workflow files, so no CI run exists yet.

Treat the code as a complete first draft that needs its first build. Compiler errors on that build are likely.

## What exists

| Part | State |
| --- | --- |
| `Jarvis.Core`, `Jarvis.Infrastructure`, `Jarvis.AI`, `Jarvis.Agent`, `Jarvis.Windows`, `Jarvis.Desktop` | Written (71 C# files, 4 XAML files, about 12,000 lines, 70 tools) |
| `tests/Jarvis.Tests` | Written: 17 files, 229 facts and 28 theories with 140 cases |
| `installer/Jarvis.iss` | Written |
| `ci/jarvis-windows-build.yml` | Written; **not active** until copied to `.github/workflows/` |
| `README.md`, `docs/ARCHITECTURE.md` | Written |

## What was checked, and how

No compiler was available, so six best-effort static checkers were written (`tools/`) and run over the code. All
report zero findings on the committed state.

| Checker | What it catches | Result |
| --- | --- | --- |
| `check_syntax.py` | C# syntax errors (tree-sitter parse) | 88 files, 0 problems |
| `check_members.py` | Calls to members that do not exist on the project's own types, wrong argument counts, unimplemented interface members | 244 types, 7140 member uses, 0 findings |
| `check_usings.py` | Type names used without the matching `using` | 0 findings |
| `check_tool_calls.py` | Tool names and argument names in locally built tool calls that do not match a declared tool | 70 tools, 53 calls, 0 findings |
| `check_xaml.py` | XAML bindings without a matching view-model property, missing resources, missing event handlers | 131 bindings, 249 resource references, 0 findings |
| `check_sql.py` | SQL that does not parse against the schema (run in real SQLite) | 17 tables, 58 statements, 0 failed |

In addition, the schedule parser and the offline command interpreter were ported to Python and run against 13 schedule
cases and 77 command phrases, including every phrase the tests and the smoke test rely on: 0 mismatches.

NuGet package versions referenced by the projects were confirmed to exist on nuget.org.

### What these checks cannot see

- Type errors: argument types, generics, nullability, overload resolution, ambiguous references.
- Any use of external APIs: WPF, Windows Forms, System.Speech, System.Management, Microsoft.Data.Sqlite, PdfPig,
  P/Invoke signatures.
- Whether dependency injection can construct every service.
- Run-time behaviour of any kind. No test has been executed, so no test is known to pass.

## Not verified

- The solution builds.
- Any test passes.
- The app starts, shows its windows, registers hotkeys or the tray icon.
- Voice input and output on a real machine.
- The Gemini request and response format against the live API. The client was written from the published
  documentation for the Interactions API; no request has been sent.
- The installer compiles, installs, upgrades or uninstalls.
- The CI workflow itself.

## To get to a verified build

1. Copy `desktop/jarvis/ci/jarvis-windows-build.yml` to `.github/workflows/jarvis-windows-build.yml` and push
   (this needs a credential with permission to change workflows).
2. Fix whatever the first build and test run report.
3. When the workflow is green, download the installer artifact and try it on a real machine: API key, a Gemini
   question, a reminder, voice, hotkeys, restart behaviour.

## Known limitations

- Windows x64 only.
- Speech recognition uses the engine built into Windows (System.Speech). Accuracy depends on the installed language
  pack and microphone, and is below cloud dictation.
- No wake word; voice input is hold-to-talk.
- The offline interpreter understands a fixed set of English phrasings. Anything else needs Gemini.
- Full-text search covers plain text, source code, PDF text and Word (.docx) files. Other Office formats and scanned PDFs are not indexed.
- The installer is not code-signed.
- The default Gemini model name and API revision are settings (Settings › Gemini), since Google renames models.
