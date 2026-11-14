# Security

## Threat model

Jarvis is an assistant that can read screens and act inside other apps. That makes it a target in a
way an ordinary app is not, so the threats are named explicitly rather than assumed away.

| Threat | Mitigation |
|---|---|
| A consequential action runs without the user meaning it | Confirmation tiers; `confirmConsequentialActions` defaults to `true`; the parse result records `alternatives` when the intent is ambiguous, so the agent asks instead of choosing |
| A mis-parse becomes a destructive action | `IntentKind` is a closed enum of 70 kinds — an intent cannot be invented by a mis-parse; a model reranker may reorder candidates but never add one |
| A secret phrase leaks into logs, memory or a crash report | `AUTHENTICATE` parses to zero entities and is flagged `sensitive`; only a PBKDF2 verifier and salt are stored |
| Memory poisoning — a wrong "fact" silently steering later behaviour | Explicit `remember` is distinguishable from inference; `inferPreferences` can be disabled; near-duplicates merge instead of stacking; `forget` and `forgetAll` are real deletions with index rebuild |
| Data exfiltration | No analytics or telemetry code; online providers off by default; `describeOutgoingPayload` exists so the app can show what would leave before it does |
| Cloud backup quietly copying personal data | `allowBackup="false"` plus explicit backup and data-extraction rules |
| A dependency doing something the app did not ask for | Zero third-party runtime dependencies; the only external artifact is `junit`, for tests |
| Prompt injection through observed screen text | Screen content is data, never instructions: it becomes entities and node text, not intents. Only the user's own request produces an intent. (Enforced by the closed intent vocabulary and by the agent loop's VERIFY step.) |
| A tool exceeding its grant | Every port returns `PortResult.Denied(reason, capabilityId)`; denial is a normal outcome that the agent reports, not an exception that gets swallowed |

## Permission matrix

Declared in `app/src/main/AndroidManifest.xml`. Each permission unlocks one specific capability, and
the in-app permissions screen is required to explain exactly that.

| Permission | Why | Sensitive? |
|---|---|---|
| `ACCESS_NETWORK_STATE` | Detect connectivity so a networked feature can be labelled `ONLINE REQUIRED` | no |
| `INTERNET` | Only used by an online provider you explicitly enable | yes — off by default |
| `RECORD_AUDIO` | Push-to-talk, voice conversation, optional wake word | yes |
| `POST_NOTIFICATIONS` | The persistent assistant notification and reminder/alarm notifications (API 33+) | no |
| `FOREGROUND_SERVICE`, `_MICROPHONE`, `_SPECIAL_USE` | Keep listening and pending timers alive with the screen off | yes |
| `WAKE_LOCK` | Fire scheduled work on time | no |
| `VIBRATE` | Haptic confirmation of an action | no |
| `SCHEDULE_EXACT_ALARM` | Reminders and alarms at the exact time asked for; falls back to inexact scheduling when denied | no |
| `RECEIVE_BOOT_COMPLETED` | Re-arm reminders and automations after a reboot | no |
| `READ_CONTACTS` | Resolve "message Rahul" to a real contact | yes |
| `BLUETOOTH_CONNECT` | Only for a user-created automation such as "when I connect my headphones" | yes |
| `ACCESS_COARSE_LOCATION`, `ACCESS_FINE_LOCATION` | Only for user-created location automations; never for tracking | yes |

Scoped package visibility is declared with `<queries>` for launchable apps, `SENDTO`/`smsto`,
`SEND text/plain`, `RecognitionService`, `TTS_SERVICE` and `MediaBrowserService`.

### Deliberately not requested

| Permission | Why not |
|---|---|
| `QUERY_ALL_PACKAGES` | Blanket package visibility is not needed; a scoped `<queries>` block sees launchable apps |
| `READ_SMS` | Reading private messages is out of scope by design |
| `SEND_SMS` | Sending is delegated to the user's own messaging app through an intent, so the user sees and confirms it |
| `CALL_PHONE` | Calls are delegated to the dialer; Jarvis does not place calls silently |
| `ACCESS_BACKGROUND_LOCATION` | Never needed. Location automations run in the foreground only |
| `BIND_ACCESSIBILITY_SERVICE`-style system grants, device-admin, `MANAGE_DEVICE_POLICY_*` | Jarvis does not try to become a device owner or bypass the lock screen |

Requests for `MAKE_CALL`, `SEND_MESSAGE` and `READ_MESSAGES` **are** recognised by the NLU. That is
intentional: recognising them lets Jarvis explain the limitation and hand off to the user's own phone
or messaging app, rather than falling through to small talk and hiding that the request was
understood.

### Not declared yet

The accessibility service and the notification listener service are the mechanism for Stage 7
(screen observation) and have not been added to the manifest at this commit. Both require an explicit
user journey through system Settings, and neither can be granted at runtime by the app.

## The secret phrase

A second factor for consequential actions — deleting data, exporting, changing safety settings.

- The phrase is **never stored**. A PBKDF2-derived verifier and a random salt are stored instead, via
  `SecretVault`.
- Derivation and comparison use the pure-Kotlin `Pbkdf2`, `Sha256`, `HmacSha256` and
  `constantTimeEquals` in `core/…/crypto/Crypto.kt`. Constant-time comparison is not optional: a
  byte-by-byte compare leaks the verifier through timing.
- `RandomBytesPort` supplies salt material, with a deterministic `FixedRandomBytes` for tests so
  verification is reproducible offline.
- Android Keystore protects the stored verifier where the device supports it; the design degrades to
  file storage with the verifier still being useless without the phrase.
- **The phrase gates Jarvis, never the device.** It cannot unlock the phone, cannot read data before
  the user unlocks, and cannot survive a lock-screen policy. That distinction is the whole point: an
  assistant that could bypass the lock screen would be a hole in the platform's security model.

## What Android does not allow

Recorded in the capability catalogue as `NOT_POSSIBLE` with an `androidLimitation` explaining why. No
workaround is claimed for any of these.

- **Bypassing the lock screen.** An app cannot dismiss the keyguard or read protected data while the
  device is locked. Jarvis can run scheduled work and, with a foreground service, keep a microphone
  session alive, but it cannot act as an unlocked device.
- **Reading or sending SMS** without the user's own messaging app.
- **Placing a call** without the dialer.
- **Clicking or typing in another app** without the accessibility permission being granted by the
  user in system Settings — and even then, some system surfaces (the lock screen, the permissions
  dialogs themselves, secure text fields flagged `FLAG_SECURE`) remain off limits.
- **Reliable always-on wake-word detection** without either a foreground microphone service or a
  device-specific hotword API. Where neither is available the status is `DEVICE_DEPENDENT`, and
  push-to-talk is offered instead of pretending the wake word works.
- **Guaranteed exact alarms** on Android 12+ without `SCHEDULE_EXACT_ALARM`, which the user can
  revoke. Denial is detected and scheduling falls back to inexact windows, with the change reported.

## Reporting a problem

Security reports should describe the capability that misbehaved and the `capabilityId` shown in the
app's status output — that id maps directly to the catalogue entry, its status and its stated
limitation, which makes a report reproducible.
