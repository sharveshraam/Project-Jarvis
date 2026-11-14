# Roadmap

Fifteen incremental stages. Each one ends with the build green, its own tests passing, and the docs
updated — the next stage does not start on a broken foundation.

Status legend: ✅ complete · 🚧 in progress · ⬜ not started.

---

## ✅ Stage 0 — Scaffold and offline verification

**Done means:** the repository builds and proves it builds, with no Android SDK and no network.

- Two modules: `:core` (pure Kotlin/JVM, zero Android imports) and `:app` (Android, framework only).
- Version catalog declaring **no third-party runtime dependencies**, with the reasoning recorded
  in-file.
- `tools/local_core_check.sh` — compile and run `:core` suites with `kotlinc` and a JRE.
- `tools/validate_android.py` — resources, manifest components and view references checked by parsing
  the XML directly.
- `tools/local_android_check.sh` — compile `:core` + `:app` against the real `android.jar` (API 35)
  with `-no-jdk`, generating an offline `R` class first.
- `tools/verify_all.sh` — all four, in CI order.
- `tools/build_apk_offline.sh` — build a real, installable debug APK (aapt2 → kotlinc → d8 →
  zipalign → apksigner) with no Gradle, no SDK and no network. Added after the sandbox turned out to
  block every Maven and SDK host; it also found a resource defect the type-check could not see (see
  `docs/DEVELOPMENT.md` → "It found a real bug").
- `tools/validate_workflow.py` — checks `.github/workflows/build-apk.yml` offline: triggers,
  permissions, artifact name and path, version agreement with `gradle/`, banned failure-hiding
  patterns, and `bash -n` plus strict mode on every embedded shell script.
- `.github/workflows/build-apk.yml` — live GitHub Actions workflow (`Build Android APK`): offline
  verification, then a real Gradle `:app:assembleDebug` published as the `android-debug-apk`
  artifact. No Android Studio, no local SDK and no signing material required.

## ✅ Stage 1 — Application shell

**Done means:** an installable app that renders and behaves honestly about what is not wired up yet.

- `JarvisApplication`, `JarvisGraph`, `MainActivity` with `findViewById` (view binding deliberately
  disabled — it would require AndroidX).
- Transcript surface, input, send control, and the **provenance banner** every response must respect
  (`LOCAL` vs `ONLINE`).
- Manifest: scoped `<queries>` instead of `QUERY_ALL_PACKAGES`, `allowBackup="false"` with backup and
  data-extraction rules, `ASSIST` intent filter.
- The microphone control is **disabled**, not fake: voice arrives in Stage 6.
- Notification channel contract plus its suite.

## ✅ Stage 2 — Core foundations

**Done means:** configuration, capability honesty, crypto, ports and real memory — all tested offline.

- `AssistantConfig`: nickname (user-chosen, renamable, coupled to the wake word), response and
  learning style, privacy flags defaulting to off, editable `daypartHours`, provider slots. Tolerant
  JSON loading: unknown enums fall back, out-of-range numbers clamp.
- `Crypto.kt`: SHA-256, HMAC-SHA256, PBKDF2, `constantTimeEquals`, `RandomBytesPort` — pure Kotlin.
- **Capability catalogue**: 60 entries over 8 statuses (`SUPPORTED`, `PARTIALLY_SUPPORTED`,
  `PERMISSION_REQUIRED`, `ONLINE_REQUIRED`, `DEVICE_DEPENDENT`, `MODEL_REQUIRED`, `DISABLED_BY_USER`,
  `NOT_POSSIBLE`), each `NOT_POSSIBLE` naming its `androidLimitation`. `CapabilityResolver` layers
  user configuration on top of the device probe.
- **Ports** — 36 interfaces across storage, device, voice, screen and AI, all returning `PortResult`
  where `Denied` carries a `capabilityId`.
- **Memory**: five categories, hybrid retrieval (0.55 vector + 0.45 BM25 with a relevance gate before
  importance/recency/pin boosts apply), dedupe-and-merge at 0.90 similarity, topic keys, forget by id
  / topic / category / all, expiry pruning, JSON export and import, profile summary.
- In-memory implementations of every storage port, so behaviour is testable with no device.

## ✅ Stage 3 — Natural-language understanding

**Done means:** a sentence becomes a `ParsedRequest` with an intent, arguments, time and steps —
offline, deterministically, explainably.

- `IntentKind`: a closed enum of **70 kinds**, so a mis-parse cannot invent an action.
- `IntentRules`: narrow patterns, base confidence plus small per-signal bonuses, specificity
  priorities, negation guards, and a `reason` string on every hit naming the phrase that matched.
- `EntityExtractor`: apps and aliases, URLs, ordinals, numbers, percentages, levels, directions,
  settings and values, quoted spans, screen elements, contacts, topics, assistant names, preferences,
  documents, automation trigger/action, arithmetic expressions, unit conversions.
- `TemporalParser`: durations (fractional, compound, articles, "1.5 hours"), clock times (spoken
  forms, 24-hour, meridiem inference), day offsets and names, dayparts read from the user's own
  schedule, recurrence **with real intervals**, and human-readable descriptions.
- `NluEngine`: wake-word stripping, multi-step decomposition (only when both halves stand alone),
  context-sensitive reading of short replies, ambiguity surfaced as `alternatives`, missing slots
  reported as notes, and `sensitive` flags for authentication and memory opt-outs.
- `IntentReranker`: the seam where an on-device or online model may reorder candidates — it can never
  add one, and a failure degrades to the deterministic result.
- 139 new checks. Bugs found and fixed rather than worked around are listed in the Stage 3 commit.

## ⬜ Stage 4 — Tool system

**Done means:** every action is a named, permission-aware tool with a declared risk tier.

- Tool registry: `open_app`, `read_screen`, `click_element`, `type_text`, `scroll`, `go_back`,
  `go_home`, memory operations, timers and reminders, media, volume, settings, notifications.
- Each tool declares required capabilities, its confirmation tier, whether it is reversible, and what
  "success" looks like so the agent loop can VERIFY it.
- Intent → tool routing as a table lookup from `IntentKind`.
- Denial is a result, not an exception: a tool that cannot run returns the capability id and remedy.

## ⬜ Stage 5 — Persistence

**Done means:** every storage port has a real SQLite implementation and data survives a reboot.

- `SQLiteOpenHelper` schema with migrations for config, memory, conversation, tasks, schedule, notes,
  automations, learning, knowledge, vectors and the secret vault.
- Android adapters for the device ports used so far.
- Round-trip tests: write, kill the process, read back.
- Export and delete wired to the real store.

## ⬜ Stage 6 — Voice

**Done means:** you can talk to it and it talks back, with the wake word working where the platform
allows.

- `TextToSpeechPort` and `SpeechToTextPort` over the platform services; push-to-talk first.
- Foreground microphone service so listening survives the screen turning off.
- Wake word where a hotword API or a foreground service makes it possible; `DEVICE_DEPENDENT` with an
  honest explanation where it does not.
- `speakResponses`, `ttsVoiceId`, pitch and rate from config; transcripts only if
  `voiceHistoryEnabled` is on.

## ⬜ Stage 7 — Screen observation

**Done means:** Jarvis can describe what is on screen in any app.

- `AccessibilityService` declared and explained, with the system-Settings grant journey.
- Screen model: nodes, bounds, text, clickable/scrollable/editable flags, package and window.
- `read_screen` returning a structured description, not a screenshot dump.
- Secure surfaces (`FLAG_SECURE`), the lock screen and permission dialogs reported as off limits.

## ⬜ Stage 8 — Screen actions and the agent loop

**Done means:** OBSERVE → UNDERSTAND → PLAN → ACT → VERIFY → CONTINUE, with recovery.

- Gestures and text injection through the accessibility service: tap, long-press, type, scroll,
  swipe, back, home, recents.
- Planner turning a multi-step `ParsedRequest` into ordered tool calls with tiers attached.
- **VERIFY** by re-observing: did the screen change, did the field take the text.
- Failure recovery that re-plans from the current observation instead of repeating a failed step.

## ⬜ Stage 9 — Scheduling

**Done means:** timers, reminders and alarms fire at the right time, including after a reboot.

- `AlarmManager` exact alarms with detection of a revoked `SCHEDULE_EXACT_ALARM` and an honest
  fallback to inexact windows.
- `JobScheduler` for deferred and recurring work; boot receiver re-arming everything.
- Recurrence honoured from `Recurrence`, including interval rules.
- Snooze, cancel, list.

## ⬜ Stage 10 — Automation engine

**Done means:** user-defined trigger → condition → action rules that the user can read and control.

- Triggers: time, connectivity, charging, headphones, location (foreground only), app opened,
  notification arrived.
- Conditions and actions expressed as tool calls, so an automation cannot do anything a request
  cannot.
- Every rule inspectable: what triggers it, what it will do, whether it is enabled, and its run
  history with outcomes.
- Creation from natural language via `CREATE_AUTOMATION` and its trigger/action entities.

## ⬜ Stage 11 — Teaching mode

**Done means:** interactive lessons with tracked progress, working offline.

- Lesson model, difficulty levels, learning styles from config.
- Explanation, practice questions, answer evaluation and mistake explanation.
- `LearningStore` progress: topics, attempts, accuracy, streaks, what to revise next.
- Spaced repetition driven by the memory layer's importance and recency signals.

## ⬜ Stage 12 — Knowledge ingestion

**Done means:** your PDFs and notes become searchable locally.

- `PdfRenderer` and text extraction; chunking with overlap.
- Embeddings from the existing hashing embedder plus BM25, stored through `VectorStore` and
  `KnowledgeStore`.
- Retrieval answering `QUERY_KNOWLEDGE` with citations back to the source document and location.
- Per-document delete, and a listing of everything ingested.

## ⬜ Stage 13 — AI slots

**Done means:** optional intelligence, clearly labelled, never required.

- `LocalModelPort` for an on-device model (MediaPipe LLM Inference / Gemini Nano where available),
  with install, size and RAM reporting, and `MODEL_REQUIRED` when absent.
- `OnlineAiPort` and `WebSearchPort` for user-configured providers, with
  `describeOutgoingPayload` shown before anything leaves the device.
- `IntentReranker` wired to whichever provider is active — reordering only, never inventing.
- Provenance on every response; offline behaviour unchanged when both are off.

## ⬜ Stage 14 — Safety

**Done means:** consequential actions are gated, explained, reversible where possible, and logged.

- Confirmation tiers derived from reversibility and blast radius; `confirmConsequentialActions`
  respected and surfaced in Settings.
- Secret-phrase gate over PBKDF2 with Keystore protection for the highest tier.
- Undo where an action is undoable, and an explicit statement where it is not.
- Activity log: what was asked, what was decided, which tool ran, what the result was, and why —
  excluding anything flagged `sensitive`.

## ⬜ Stage 15 — Controls, polish and hardening

**Done means:** the user is in charge, and the docs match the build.

- Settings UI for every flag in `AssistantConfig`, the permissions screen, and the capability browser
  showing real statuses with remedies.
- Export, import, delete-all and disable-everything flows reachable from the UI.
- First-run explanation of what the assistant will and will not do.
- Hardening pass: input limits, rate limits on destructive operations, crash-safe writes.
- Documentation reconciled against the shipped behaviour.

---

## Rules that apply to every stage

1. **Never fake functionality.** A button that does nothing is disabled and says why; a capability
   that Android forbids is `NOT_POSSIBLE` with the limitation named.
2. **Do not advance on a broken foundation.** `./tools/verify_all.sh` must pass before the next stage
   starts.
3. **Test first, in the same commit.** A stage without a suite registered in `AllSuites` is not done.
4. **Document Android's limits as they are discovered**, in the capability catalogue and in
   `SECURITY.md`.
5. **Privacy and security claims are checked against code.** If a change would weaken a statement in
   `PRIVACY.md` or `SECURITY.md`, that file is updated in the same commit.
6. **Preserve useful existing code.** Refactoring is allowed; silently deleting working behaviour is
   not.
