# Privacy

This document describes what Jarvis stores, where it goes, and how you control it. It is written to
be checkable against the code, not to be reassuring.

## The short version

- **No account.** Nothing requires signing in, and there is no server to sign into.
- **No analytics, no crash reporting, no telemetry.** There is no such code and no dependency that
  could add it — the project has zero third-party runtime dependencies.
- **No silent uploads.** Network use is limited to providers *you* enable, and every one of them is
  off by default.
- **Local storage only.** Your data lives in the app's private storage on your device.
- **Everything is exportable and erasable**, including the ability to switch memory off entirely.
- **Online and local are labelled.** Every response says whether it came from `LOCAL` or `ONLINE`.

## What is stored

| Data | Category | Default | Retention |
|---|---|---|---|
| Your name, and facts you ask to be remembered | `USER_PROFILE`, `LONG_TERM` | on | until you delete it |
| Preferences ("I prefer dark mode", your day-part schedule) | `PREFERENCE` | on | until you delete it |
| Tasks, reminders, notes | `TASK` | on | until completed or deleted |
| Recent conversation context | `CONVERSATION_CONTEXT` | on | temporary — 6 hours, then pruned |
| Your documents, chunked and indexed | knowledge base | off until you import | until you delete the document |
| Voice transcripts | `voiceHistoryEnabled` | **off** | off means nothing is kept |
| Configuration, nickname, wake word | config | on | until reset |
| Secret-phrase verifier | `SecretVault` | only if you set one | PBKDF2 verifier + salt, never the phrase |

Memory records carry an `importance`, a topic key, tags, an embedding vector, a recall count and an
optional expiry. The embedding is computed on-device by a deterministic hashing embedder — it is not
sent anywhere, and no external model is required to produce it.

### What is deliberately never stored

- **Secret phrases.** `AUTHENTICATE` requests are flagged `sensitive` in the parse result, and the
  phrase is extracted into *no* entity. Only a PBKDF2 verifier and salt are written.
- **Requests you opt out of.** "Don't remember this" sets `DO_NOT_REMEMBER`, which is itself flagged
  `sensitive` so the opt-out is honoured rather than recorded as content.
- **SMS bodies, call logs, or message content.** Jarvis does not hold `READ_SMS`, `SEND_SMS` or
  `CALL_PHONE`. Those capabilities are reported as `NOT_POSSIBLE` with the reason recorded, not
  quietly requested later.
- **Background location history.** Location permissions exist only so *you* can create a "when I
  arrive at college" automation. `ACCESS_BACKGROUND_LOCATION` is never requested.

## Where it is stored

The app's private storage, on the device. `android:allowBackup="false"` is set in the manifest, and
`res/xml/backup_rules.xml` / `data_extraction_rules.xml` exclude the assistant's data from cloud
backup and from device-to-device transfer, so your memory does not leave the phone through a path you
did not choose.

Storage implementations sit behind the `StoragePorts` interfaces. `:core` ships in-memory
implementations for testing; `:app` supplies SQLite-backed ones (Stage 5). Swapping the database
changes no logic, because nothing outside the port knows how records are kept.

## Your controls

Implemented in `MemoryManager` and `AssistantConfig` today:

| Control | Effect |
|---|---|
| `remember …` / `forget …` | Add or remove a specific memory. "Forget my card number" targets the topic, not everything. |
| `forgetCategory` | Erase a whole category, e.g. all conversation context. |
| `forgetTemporary` | Erase only the short-lived conversation context. |
| `forgetAll` | Erase every memory record. |
| `exportJson` | A complete JSON export of everything stored, readable by you and re-importable. |
| `importJson` | Restore from an export. |
| `pruneExpired` | Drop records past their expiry. |
| `memoryEnabled = false` | Stop storing new memories entirely. |
| `inferPreferences = false` | Stop deriving preferences from conversation; only explicit statements are kept. |
| `voiceHistoryEnabled = false` | Keep no transcripts (this is the default). |
| `onlineAiEnabled = false` | No network calls to any AI provider (this is the default). |
| `onlineSearchEnabled = false` | No web search (this is the default). |

Deleting data is a real deletion, not a hide: records are removed from the store and the retrieval
index is rebuilt (`rebuildIndex`) so they cannot be recalled.

## Network

The `INTERNET` permission is present because an online provider is a supported option — not a default
one. With `onlineAiEnabled` and `onlineSearchEnabled` both false, the app makes **no network calls**.
`ACCESS_NETWORK_STATE` is used to detect connectivity so that a request needing the network can be
labelled `ONLINE REQUIRED` instead of failing mysteriously.

When you do enable an online provider:

- The provider is identified by `onlineProviderId`, configured by you.
- `OnlineAiPort.describeOutgoingPayload(request)` exists specifically so the app can show you what is
  about to leave the device *before* it leaves.
- Every response is labelled `ONLINE`. Local results are labelled `LOCAL`. The two are never blended
  silently.

## Screens and other apps

Reading the screen requires the accessibility permission, which Android grants only through a
deliberate journey in system Settings. When granted, screen content is used to answer your request
and to act on your behalf. Screen captures and node text are treated as memory-eligible data, which
means the same controls apply: `memoryEnabled = false` stops retention, `forget` removes it, and
`exportJson` shows you exactly what was kept.

Nothing observed on screen is uploaded. There is no code path that could do so without an online
provider being enabled by you first.

## Current status, honestly

Privacy *controls* exist as tested logic in `:core`. The Android app is still a shell (Stage 1) and
SQLite persistence arrives in Stage 5, so **at this commit there is no long-lived personal data on a
device yet** — the in-memory stores used by the test suites are process-local and die with the
process. This document describes the design that is implemented in code and verified by 247 offline
checks; it will be updated as each storage-backed stage lands.

If a future change would weaken any statement here, the change is not acceptable without updating
this file in the same commit.
