# Jarvis

A personal AI assistant for Android — a **personal operating layer**, not a chatbot and not a
launcher.

Jarvis is meant to *do things on your phone*: understand what you ask in ordinary language, look at
the screen you are on, act inside other apps, remember what matters to you across weeks, run the
automations you define, and teach you things — all while keeping your data on your device and
telling you the truth about what it can and cannot do.

```
you:  hey jarvis, remind me to submit the lab report tomorrow at 9am
      and then open the camera

jarvis: Two things.
        1. Reminder "submit the lab report" — tomorrow at 9:00 AM.   [needs notification access]
        2. Open Camera.
        Shall I go ahead?                                            [CONFIRM: consequential]
```

That exchange is the **target**, shown to make the design concrete. The understanding half of it is
built and tested today — the two steps, the reminder text with its time removed, `tomorrow at 9:00 AM`
resolved from your own schedule, and the capability label all come out of `:core` right now. Acting on
it arrives with the tool system, scheduling and safety stages (4, 8, 9, 14).

---

## Current status — read this first

This repository is being built in **15 incremental stages** ([docs/ROADMAP.md](docs/ROADMAP.md)).
Stages 0–3 are complete. **The assistant is not yet usable as an assistant**: the Android app is a
verified shell, and the intelligence built so far lives in `:core` where it is fully tested offline
but not yet wired to the screen, the microphone or the accessibility service.

| Area | State | Where |
|---|---|---|
| Build, offline verification, CI definition | **Done** | `tools/`, `ci/` |
| Android app shell (activity, theme, transcript, provenance banner) | **Done** | `app/` |
| Configuration, nickname renaming, day-part schedule | **Done** | `core/…/config/` |
| Capability catalogue with honest status labels (60 capabilities) | **Done** | `core/…/capability/` |
| Ports — the seams every subsystem plugs into (36 interfaces) | **Done** | `core/…/ports/` |
| SHA-256 / HMAC / PBKDF2 in pure Kotlin, constant-time compare | **Done** | `core/…/crypto/` |
| Structured local memory: hybrid vector + BM25 retrieval, forget, export | **Done** | `core/…/memory/` |
| Natural-language understanding: 70 intents, entities, time, multi-step | **Done** | `core/…/nlu/` |
| Tool system, agent loop, screen reading and in-app actions | Not started | Stage 4, 7, 8 |
| SQLite persistence, voice, scheduling, automation, teaching, knowledge | Not started | Stage 5, 6, 9–12 |
| On-device model slot, optional online providers | Not started | Stage 13 |
| Confirmation tiers, secret-phrase gate, undo, activity log | Not started | Stage 14 |

Nothing in this repository fakes a capability. Where Android does not allow something, the code says
so and the reason is recorded in the capability catalogue rather than papered over with a stub that
silently does nothing.

## What it is designed to be

- **Intent understanding, offline.** A deterministic recogniser over a closed vocabulary of 70
  intents, with slot extraction and time parsing. No model download, no network, and every match
  carries the phrase that triggered it.
- **On-screen observation and action.** Read what is on screen through `AccessibilityService`, then
  tap, type, scroll and navigate — inside other apps, not just this one.
- **Persistent structured memory.** Five categories (profile, preferences, long-term, tasks,
  conversation context) with hybrid retrieval, explicit `remember` / `forget`, and full
  export/import/delete.
- **Your assistant, your name for it.** The nickname is user-chosen, changeable at any time, and no
  state keys off it. Renaming also renames the wake word.
- **Voice.** Wake word, speech-to-text and text-to-speech through the platform's own services.
- **Locked-phone operation, within Android's limits.** Jarvis can run scheduled work and voice
  interaction while the screen is off. It **cannot** and will not bypass the lock screen — that is
  recorded as `NOT_POSSIBLE` in the capability catalogue, not hidden.
- **Secret-phrase gate.** A second factor for consequential actions, stored only as a PBKDF2
  verifier plus salt. It gates *Jarvis*, never the device.
- **Teaching mode.** Interactive lessons with tracked progress, worked offline from your own
  documents.
- **Optional online AI, always labelled.** `onlineAiEnabled` defaults to **false**. Every response
  carries its provenance: `LOCAL` or `ONLINE`.
- **Knowledge ingestion.** PDFs and notes chunked, embedded and indexed locally for retrieval.
- **Automation you can inspect.** Trigger → condition → action rules that the user can read, edit,
  disable and see the run history of.
- **Safety tiers.** Reading the screen and opening an app are not the same risk as deleting data or
  sending a message; confirmation requirements follow from that.
- **Privacy-first by construction.** No account, no analytics, no silent uploads, local storage
  only, and export/delete/disable controls.

## Repository layout

```
core/    pure Kotlin/JVM. No Android imports at all.
         config, crypto, capability, ports, model, memory, nlu, store, util, testing
app/     the Android application. Framework APIs only — no AndroidX, no third-party libraries.
         Adapts Android services to the ports :core declares.
tools/   offline verification: compile + run the suites with no SDK, no Gradle, no network.
ci/      the GitHub Actions workflow definition (see ci/README.md to enable it).
docs/    ARCHITECTURE, PRIVACY, SECURITY, DEVELOPMENT, TESTING, ROADMAP.
```

The split is the core architectural decision: everything that makes Jarvis *intelligent* is ordinary
Kotlin that compiles and tests on a laptop in milliseconds, and `:app` is a thin adapter over
platform services. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Verify it yourself

No Android SDK, no Gradle and no network are required:

```bash
./tools/verify_all.sh
```

That runs, in order:

1. **`:core` offline behavioural suite** — 246 checks, compiled with `kotlinc` and run on the JVM.
2. **Android resource validation** — every `R.layout`/`R.id` reference exists, every manifest
   component resolves, every declared view binding is real.
3. **Android type-check + app suite** — `:core` and `:app` compiled together against the *real*
   `android.jar` for API 35, then the app's own checks run on the JVM.

Individual stages: `./tools/local_core_check.sh`, `./tools/validate_android.py`,
`./tools/local_android_check.sh`.

With a normal Android toolchain the same suites run under Gradle:

```bash
./gradlew :core:test :app:test       # identical assertions, via a thin JUnit bridge
./gradlew assembleDebug              # unsigned debug APK
```

Details, including how to build without network access, are in
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) and [docs/TESTING.md](docs/TESTING.md).

## Documentation

| Document | What it covers |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Modules, ports, data flow, the agent loop, dependency policy, replaceability |
| [docs/PRIVACY.md](docs/PRIVACY.md) | Exactly what is stored, where, for how long, and how to export or erase it |
| [docs/SECURITY.md](docs/SECURITY.md) | Permission matrix, threat model, secret-phrase design, what Android forbids |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | Toolchain, offline verification, conventions, adding a subsystem |
| [docs/TESTING.md](docs/TESTING.md) | Suite structure, what is covered, and what is honestly not covered yet |
| [docs/ROADMAP.md](docs/ROADMAP.md) | The 15 stages, what "done" means for each, and current progress |

## Requirements

- Android 8.0 (API 26) or newer; targets API 35.
- No account. No internet connection needed for anything except the features explicitly labelled
  `ONLINE REQUIRED`, which stay off until you turn them on.

## Licence

MIT — see [LICENSE](LICENSE).
