# Architecture

## The one decision everything else follows from

`:core` is pure Kotlin/JVM and contains **no Android imports whatsoever**. `:app` is a thin adapter
that implements `:core`'s ports using Android framework services.

```
        ┌─────────────────────────────────────────────┐
        │  :core   pure Kotlin, no Android, no deps   │
        │                                             │
        │  config · crypto · capability · model       │
        │  memory · nlu · (tools · agent · safety)    │
        │                                             │
        │        declares ports, owns all logic        │
        └───────────────────▲─────────────────────────┘
                            │ implements
        ┌───────────────────┴─────────────────────────┐
        │  :app    Android framework APIs only        │
        │                                             │
        │  AccessibilityService · SQLiteOpenHelper    │
        │  TextToSpeech · SpeechRecognizer            │
        │  AlarmManager · JobScheduler · MediaController│
        │  PdfRenderer · KeyguardManager · ContentResolver│
        └─────────────────────────────────────────────┘
```

Why this shape:

- **Testability.** Understanding, memory, planning and safety are the parts with the most ways to be
  subtly wrong. They compile and run as ordinary JVM code in under a second, with no emulator.
- **Replaceability.** A port is a seam. Swapping SQLite for something else, or the platform TTS for
  another engine, means writing one class — no logic moves.
- **Auditability.** With zero third-party runtime dependencies there is no supply chain to trust and
  the whole app type-checks against `android.jar` alone.
- **Honesty about the platform.** Android-specific limits are discovered in `:app` and reported
  through the capability catalogue, rather than being baked into the logic where they would be
  invisible.

## Dependency policy

There are **no third-party runtime dependencies**. This is deliberate, and recorded in
`gradle/libs.versions.toml`. Everything needed exists in the Android framework:

| Need | Framework API |
|---|---|
| Screen reading and gestures | `AccessibilityService` |
| Notifications | `NotificationListenerService` |
| Speech in / out | `SpeechRecognizer`, `TextToSpeech` |
| Persistence | `SQLiteOpenHelper` |
| Exact and deferred scheduling | `AlarmManager`, `JobScheduler` |
| Media and volume | `MediaController`, `AudioManager` |
| Documents | `PdfRenderer` |
| Biometrics and keyguard state | `BiometricPrompt`, `KeyguardManager` |
| Contacts and messaging | `ContentResolver` |
| Connectivity | `ConnectivityManager` |

Consequences worth stating plainly:

- The UI uses `android.app.Activity` and `findViewById`. **View binding is disabled**, because the
  generated classes implement `androidx.viewbinding.ViewBinding`, which would pull in AndroidX.
- The only external artifact anywhere is `junit`, and only for tests.
- `:core`'s own test framework (`core/…/testing/TestFramework.kt`) exists so the offline runner needs
  no test library at all; `CoreSelfTestJUnit` is a thin bridge so Gradle and CI run the *same*
  assertions.

## Ports

A port is an interface in `core/…/ports/` plus a `PortResult` return type that can express denial:

```kotlin
sealed class PortResult<out T> {
    data class Ok<T>(val value: T) : PortResult<T>()
    data class Denied(val reason: String, val capabilityId: String? = null) : PortResult<Nothing>()
    data class Failed(val reason: String, val retryable: Boolean = true) : PortResult<Nothing>()
}
```

`Denied` carries a `capabilityId` so the agent can answer "why not?" with the real reason and the
real remedy, instead of a generic error.

The port families, all declared and all currently exercised only through in-memory implementations
(`core/…/store/InMemoryStores.kt`):

- **Storage** — `ConfigStore`, `MemoryStore`, `ConversationStore`, `TaskStore`, `ScheduleStore`,
  `NoteStore`, `AutomationStore`, `LearningStore`, `KnowledgeStore`, `VectorStore`, `SecretVault`,
  `FileStore`.
- **Device** — `NetworkPort`, `DeviceStatusPort`, `LauncherPort`, `SettingsPort`,
  `PermissionRequestPort`, `MediaPort`, `VolumePort`, `ContactPort`, `MessagingPort`, `CallPort`,
  `NotificationPort`, `SchedulerPort`, `TimerPort`, `BiometricPort`, `DocumentReaderPort`,
  `ClipboardPort`.
- **Voice** — `SpeechToTextPort`, `TextToSpeechPort`, `WakeWordPort`.
- **Screen** — `ScreenAgentPort`.
- **AI** — `AiProvider` (base), `LocalModelPort`, `OnlineAiPort`, `WebSearchPort`.

`:core` ships in-memory implementations so behaviour can be tested without a device; `:app` supplies
the real ones backed by SQLite and framework services. A port returning `Denied` is a normal,
expected outcome — not an error path.

## Honesty as a data structure

`CapabilityStatus` is the vocabulary for what Jarvis can actually do, right now, on this device:

```
SUPPORTED · PARTIALLY_SUPPORTED · PERMISSION_REQUIRED · ONLINE_REQUIRED
DEVICE_DEPENDENT · MODEL_REQUIRED · DISABLED_BY_USER · NOT_POSSIBLE
```

`CapabilityCatalog` holds **60 capabilities**, each with an id, a label, an explanation, a remedy
where one exists, and — for `NOT_POSSIBLE` — an `androidLimitation` field naming the platform
restriction. `CapabilityCatalog.resolve(probe)` turns a device probe into a status;
`CapabilityResolver` layers the user's own configuration on top, so a feature the user switched off
reports `DISABLED_BY_USER` rather than pretending to be broken.

Two rules follow from this and are enforced in review:

1. A capability is never reported as `SUPPORTED` unless an implementation exists and is reachable.
2. `NOT_POSSIBLE` always states the Android limitation. Examples: reading SMS, placing calls without
   the user's own dialer confirmation, bypassing the lock screen, background location history.

The UI's provenance banner is the same idea applied to answers: every response is labelled `LOCAL` or
`ONLINE`, and `onlineAiEnabled` defaults to `false`.

## Understanding (`core/…/nlu/`)

```
raw text
   │
   ├─ IntentRules.stripAddress ── "hey jarvis, …" → "…"
   │
   ├─ NluEngine.splitIntoSteps ── only when both halves stand alone as requests
   │
   ├─ IntentRules.match ─────────→ ranked RuleHit(kind, confidence, reason, entities)
   │        │
   │        └─ IntentReranker (optional model) — may reorder, may never invent
   │
   ├─ EntityExtractor ───────────→ app, contact, setting, level, direction, ordinal,
   │                               topic, query, document, URL, preference, trigger/action
   │
   └─ TemporalParser ────────────→ TimeReference.At | In | Daypart | RecurringAt | Unresolved

ParsedRequest(intent, confidence, entities, alternatives, steps, time, durationMillis, notes,
              sensitive, fromVoice)
```

Design points that are load-bearing:

- **`IntentKind` is a closed enum of 70 kinds.** The router is a table lookup; a mis-parse cannot
  invent an action that no rule recognised.
- **Confidence is base + small bonuses**, and rules are narrow on purpose. A confident wrong intent
  gets executed; a narrow miss gets asked about. Two candidates within `AMBIGUITY_MARGIN` (0.08) are
  both reported in `alternatives`.
- **Every hit carries `reason`** — `matched "remind me"` — because an intent the user cannot inspect
  is an intent nobody can debug.
- **Interrogatives are not imperatives.** `QUESTION_OPENER` excludes action intents when the sentence
  begins with a question word, so "can you read my screen?" is answered rather than performed.
  `READ_SCREEN` is the deliberate exception: nobody says "read screen", they say "what's on the
  screen?".
- **Matching runs against both normalised and raw text**, because normalising folds away exactly the
  characters that carry a URL, a quoted phrase or `14:30`.
- **Time is never guessed.** An unparsable time becomes `TimeReference.Unresolved` plus a note, which
  the agent turns into "when?". A reminder that fires immediately is worse than a question.
- **A model may re-rank, never re-author.** `IntentReranker` is the seam for an on-device classifier
  or an online provider; candidates it invents are discarded and a throwing reranker degrades to the
  deterministic result.
- **`sensitive` is set for `AUTHENTICATE` and `DO_NOT_REMEMBER`** so storage and logging layers skip
  them. A secret phrase is extracted into *no* entity: copying a credential into parse results would
  spread it into confirmations, logs and memory.

## Memory (`core/…/memory/`)

Five categories (`USER_PROFILE`, `PREFERENCE`, `LONG_TERM`, `TASK`, `CONVERSATION_CONTEXT`), each
record carrying importance, a topic key, tags, an embedding, a recall count and an expiry.

Retrieval is hybrid, because neither signal alone is good enough:

```
score = 0.55 · cosine(vector) + 0.45 · BM25(lexical)
      + 0.30 · topicScore        when the topic key matches (≥0.55)
      + 0.25                     on a direct mention (fuzzy containment, either direction)
      + 0.10                     on a tag match

gate:   at least one of  semantic ≥ 0.05 · lexical ≥ 0.05 · topic · direct mention · tag

      + 0.10 · importance
      + 0.08 · recency           exp decay: 180 days permanent, 7 days temporary
      + 0.04 · recallCount/10    capped at 10 recalls
      + 0.15                     when pinned

accept when score ≥ MIN_RECALL_SCORE (0.06)
```

The **gate** is the important part. Importance, recency and pinned status are ranking refinements:
they must never create relevance out of nothing, or a pinned memory would be injected into the
context of every unrelated question. Before the gate existed, an empty-lexical query still scored
about 0.15 from boosts alone — the classic "why did it bring that up?" failure.

Every hit also carries a `reason` string ("topic match: robotics project, pinned"), so a surprising
recall can be explained to the user rather than merely observed.

Other behaviour that is implemented and tested:

- `MIN_RECALL_SCORE = 0.06`, `DEDUPE_THRESHOLD = 0.90`, `TEMPORARY_TTL_MILLIS = 6h`.
- Near-duplicate saves **merge** into the existing record rather than appending.
- `TopicKey.stripForgetPrefix` extracts what "forget my card number" is about, so deletion targets
  the right records.
- `recall(includeExpired = true)` powers "what did we talk about yesterday".
- `exportJson` / `importJson` / `forgetAll` / `forgetCategory` / `pruneExpired` are all first-class,
  because user control over stored data is a feature, not an escape hatch.

The embedder is a hashing embedder and the lexical index is BM25 — both deterministic and offline.
They are behind interfaces so a real on-device embedding model can replace them later without
touching retrieval.

## The agent loop (design; Stages 7–8)

```
OBSERVE ──► UNDERSTAND ──► PLAN ──► ACT ──► VERIFY ──► CONTINUE
   ▲                                              │
   └────────────── recover / re-plan ◄────────────┘
```

- **OBSERVE** — the accessibility tree plus device state become a `Screen` model: the nodes, their
  bounds, their text, what is clickable and what is scrollable.
- **UNDERSTAND** — the NLU pipeline above, plus memory recall for context.
- **PLAN** — a `ParsedRequest` (possibly multi-step) becomes an ordered list of tool calls, each with
  a confirmation tier attached.
- **ACT** — tools execute through ports; a `Denied` result is data, not an exception.
- **VERIFY** — re-observe and check the intended change actually happened (did the screen change?
  did the field take the text?). This is the step most assistants skip and the reason actions fail
  silently.
- **CONTINUE / recover** — on mismatch, re-plan from the current observation rather than repeating
  the failed step blindly.

## Configuration (`core/…/config/`)

`AssistantConfig` is user-editable, stored locally, and tolerant when loading: unknown enum values
fall back to defaults and out-of-range numbers are clamped, so a config written by a newer version
never bricks an older one.

Invariants that matter:

- `nickname` is entirely the user's choice; `renamedTo()` updates the nickname **and** the wake word
  together, and no state keys off either.
- Unpronounceable nicknames are **accepted with a warning** rather than rejected — it is the user's
  assistant, and a name only needs to be speakable if they want it to be.
- `onlineAiEnabled`, `onlineSearchEnabled` and `voiceHistoryEnabled` all default to `false`. Keeping
  transcripts is opt-in.
- `daypartHours` maps names like `dinner` or `early morning` to hours, because dinner is at 8pm for
  some people and 10pm for others. Time parsing reads this map.

## Crypto (`core/…/crypto/`)

SHA-256, HMAC-SHA256 and PBKDF2 implemented in pure Kotlin, plus `constantTimeEquals` and a
`RandomBytesPort` (with a deterministic `FixedRandomBytes` for tests). Pure Kotlin rather than
`java.security` so the same code is verifiable offline and behaves identically on every API level.

The secret-phrase design: only a PBKDF2 verifier and its salt are stored, never the phrase;
verification uses constant-time comparison; Android Keystore protects the verifier where available.
The phrase gates *Jarvis's* consequential actions. It is not, and cannot be, a lock-screen bypass —
see [docs/SECURITY.md](SECURITY.md).

## Where things are heading

Stages 4–15 add the tool system, SQLite-backed stores, voice, the screen agent, scheduling,
automation, teaching, knowledge ingestion, the AI slots and the safety tiers. Each lands with its own
suite registered in `AllSuites`, so the offline runner and CI can never drift apart.
See [docs/ROADMAP.md](ROADMAP.md).
