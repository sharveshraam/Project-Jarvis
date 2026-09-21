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
.github/workflows/
         build-apk.yml — builds a debug APK on every push to main (GitHub Actions).
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

1. **`:core` offline behavioural suite** — 247 checks, compiled with `kotlinc` and run on the JVM.
2. **Android resource validation** — every `R.layout`/`R.id` reference exists, every manifest
   component resolves, every declared view binding is real.
3. **Android type-check + app suite** — `:core` and `:app` compiled together against the *real*
   `android.jar` for API 35, then the app's own checks run on the JVM.
4. **Workflow validation** — `.github/workflows/build-apk.yml` is parsed and checked for banned
   patterns, version drift and broken embedded shell (`tools/validate_workflow.py`).

Individual stages: `./tools/local_core_check.sh`, `./tools/validate_android.py`,
`./tools/local_android_check.sh`.

With a normal Android toolchain the same suites run under Gradle:

```bash
./gradlew :core:test :app:test       # identical assertions, via a thin JUnit bridge
./gradlew :app:assembleDebug         # debug APK, signed with the SDK's debug key
./gradlew :app:assembleRelease       # release APK, UNSIGNED (no signing material is committed)
```

Details, including how to build without network access, are in
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) and [docs/TESTING.md](docs/TESTING.md).

## Get an APK without building one yourself

You do not need Android Studio, an Android SDK or a Java toolchain to install this app. A GitHub
Actions workflow builds a debug APK for you:

**[.github/workflows/build-apk.yml](.github/workflows/build-apk.yml)** — workflow name
**`Build Android APK`**.

### Where to find it

1. Open the repository on GitHub → **Actions** tab.
2. Pick **Build Android APK** in the workflow list on the left.
3. It runs automatically on every push to `main`. To build any other branch (or to build `main`
   again on demand), press **Run workflow** and choose the branch.

### Where to download the APK

1. Open the run (green tick = it produced an APK).
2. Scroll to **Artifacts** at the bottom of the run summary page.
3. Download **`android-debug-apk`**. It is a zip containing one file: `app-debug.apk`.
4. Copy it to the device and open it. Android will ask you to allow installation from that source;
   `adb install app-debug.apk` works too.

Artifacts are kept for 30 days and require you to be signed in to GitHub.

### What it builds

| | |
|---|---|
| Gradle task | `./gradlew :app:assembleDebug`, through the committed wrapper (Gradle 8.11.1) |
| JDK | Temurin 17 — the level `app/build.gradle.kts` and `core/build.gradle.kts` declare |
| Android SDK | `platforms;android-35` + `build-tools;35.0.0`, installed from the runner's `sdkmanager` |
| Output APK | `app/build/outputs/apk/debug/app-debug.apk` |
| Package id | `dev.jarvis.assistant.debug` (the debug build type adds a `.debug` suffix) |
| Version | `0.1.0-debug`, versionCode 1 |

Two jobs run in order:

1. **Offline verification** — the complete `:core` behavioural suite (all 247 checks, including every
   NLU test), the Android resource/manifest validation, and a type-check of `:app` against the real
   `android.jar`. No SDK needed; it fails in a couple of minutes if the foundation is broken.
2. **Build debug APK** — only if job 1 passed. Installs the SDK, runs the Gradle build, then proves
   the output really is an APK (size, ZIP structure, `AndroidManifest.xml`/`classes.dex`/
   `resources.arsc` present, `aapt2 dump badging` identity) before uploading it. Nothing is uploaded
   unless those checks pass, and a run with no APK is a failed run, never a green one.

### Cannot run Gradle at all?

```bash
./tools/build_apk_offline.sh        # -> apk/Jarvis-0.1.0-debug.apk
```

This builds the same debug APK with **no Gradle, no Android Studio and no network at build time**, by
performing the five transformations AGP would — `aapt2 compile`/`link`, `kotlinc`, `d8`, `zipalign`,
`apksigner` — against the same sources and the same values read out of `app/build.gradle.kts`. It is
for machines where `dl.google.com` and the Maven repositories are unreachable (air-gapped networks,
sandboxes). It needs the build-tools 35.0.0 binaries already on disk
(`BUILD_TOOLS_DIR=$ANDROID_HOME/build-tools/35.0.0`) and signs with a throwaway debug key generated
into `build/`. It then verifies its own output: signature schemes, badging, zip alignment, and that
every resource id compiled into the app equals the id in `resources.arsc`.

It is *not* AGP: no manifest merger (this project has exactly one manifest), no `BuildConfig`, no
lint, no R8. `./gradlew :app:assembleDebug` — or the workflow above — remains the reference build.

### What this is *not*

- **It is a debug APK.** It is signed with the Android SDK's public debug key, which every device
  accepts for sideloading but which is not a release signature. It cannot be published to Google
  Play, and it installs alongside — not over — a release build, because of the `.debug` package
  suffix.
- **No secrets and no signing configuration** are used or stored by the workflow. Nothing needs to be
  configured before the first run.
- **A signed release build is not automated yet.** `./gradlew :app:assembleRelease` produces an
  *unsigned* APK because no keystore is committed, and `app/proguard-rules.pro` plus your own
  `signingConfig` are required first. That is deliberately deferred — see
  [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md#releasing) and
  [docs/SECURITY.md](docs/SECURITY.md).

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
