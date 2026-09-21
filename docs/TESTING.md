# Testing

## Running the tests

```bash
./tools/verify_all.sh          # everything, offline, no SDK and no network
./tools/local_core_check.sh    # just the :core behavioural suite
python3 tools/validate_workflow.py --require-yaml   # the GitHub Actions workflow itself
./gradlew :core:test :app:test # the same assertions under Gradle/CI
```

Current totals: **247 `:core` checks (12 suites) and 3 `:app` checks, all passing.**

## Structure

`:core` has its own micro test framework — `core/src/main/kotlin/dev/jarvis/core/testing/TestFramework.kt`
— because the offline runner must work with no test library available:

```kotlin
object TemporalSuite : Suite("nlu/temporal") {
    init {
        test("a passed time rolls to tomorrow rather than firing now") {
            val p = parser()
            val morning = p.parse("at 7am") as TimeReference.At
            assertEquals(MIDNIGHT + DAY + 7 * HOUR, p.resolve(morning))
        }
    }
}
```

`Suite` collects `TestCase`s; `TestRunner.run(suites)` executes them and produces a `TestReport` with
per-suite pass counts and a failure list that includes the suite, the test name and the message.
Assertions: `assertTrue`, `assertFalse`, `assertEquals`, `assertNotEquals`, `assertNull`,
`assertNotNull`, `assertContains`, `assertDoesNotContain`, `assertContainsItem`, `assertGreaterThan`,
`assertThrows`, `fail`.

Two runners consume the same registry:

| Runner | Used by | Notes |
|---|---|---|
| `LocalTestMain` | `tools/local_core_check.sh` | no test library at all; exits non-zero on failure |
| `CoreSelfTestJUnit` | `./gradlew :core:test`, CI | one `@Test` that runs the whole registry and fails with the full list |

`AllSuites.all` is the only place a suite is registered. That is deliberate: with two runners, a suite
registered in one and not the other is a suite that silently stops being run.

`:app` has its own equivalent (`AppAllSuites`, `LocalAppTestMain`, `AppSelfTestJUnit`) for checks that
need `android.jar` on the classpath.

## What is covered

| Suite | Checks | What it pins down |
|---|---|---|
| `util/json` | 8 | Encoding, decoding, nesting, escapes, tolerant reads of missing and wrong-typed fields |
| `util/text` | 22 | Normalisation, tokenisation, stemming, similarity, fuzzy matching, ordinals, word numbers |
| `crypto` | 10 | SHA-256 and HMAC against known vectors, PBKDF2 derivation, constant-time comparison, deterministic random |
| `config` | 14 | Defaults, nickname renaming and validation, wake-word coupling, JSON round-trip, tolerance of unknown or out-of-range values |
| `memory/embedding` | 8 | Determinism, L2 normalisation, cosine behaviour on identical and unrelated text |
| `memory/bm25` | 10 | Term frequency, IDF, field-length normalisation, ranking order |
| `memory/topic-key` | 10 | Topic extraction, `forget`-prefix stripping, article handling that must not over-strip |
| `memory` | 25 | Hybrid retrieval and its relevance gate, dedupe-and-merge, category scoping, expiry and pruning, forget by topic, export/import round-trip, profile summary |
| `nlu/temporal` | 33 | Durations (fractional, compound, articles), clock times (spoken forms, 24-hour, meridiem), day offsets and names, dayparts from user config, recurrence with real intervals, description strings, and refusal to resolve a vague time |
| `nlu/intent-rules` | 36 | One or more real sentences per intent group, the look-alike pairs, wake-word handling, evidence strings, ambiguity surfacing, and sentences that must match nothing |
| `nlu/entities` | 34 | Every extractor: apps and aliases, URLs, ordinals, numbers, percentages, levels, directions, settings and values, quoted spans, elements, contacts, topics, assistant names, preferences, documents, automation trigger/action, arithmetic, conversions — plus what must not be extracted |
| `nlu/engine` | 37 | End-to-end parsing: multi-step decomposition and its rejection, time attached but removed from task text, missing-slot notes, conversation context, sensitivity flags, the reranker seam, the sentence the README uses as its example, and regressions |
| `app/notification-channels` | 3 | Channel ids, importance and the manifest/contract agreement |

## How the tests are written

**Expectations are computed independently.** The temporal suite fixes a clock at a known local instant
(Tuesday 2026-03-10, 09:30, UTC+05:30), derives local midnight from it arithmetically, and asserts
against values built from those constants. Reading an expectation back out of the code under test
would make the test a tautology.

**Behaviour is described in sentences.** `"forgetting everything outranks forgetting a topic"`,
`"a reranker cannot invent an intent no rule recognised"`. When a suite fails, the output should read
as a list of broken promises rather than a list of broken functions.

**Negative cases are first-class.** A recogniser that invents intents is more dangerous than one that
admits confusion, so there are explicit tests for sentences that must match *nothing*
(`"banana banana banana"`), for idioms that must not be read literally (`"call it a day"` is not a
phone call, `"press play"` is not a screen tap), and for questions that must not be executed
(`"can you read my screen"` is answered, not performed).

**Failures fixed the code, not the test.** Every bug the NLU and temporal suites found was a real
defect and was fixed in the implementation: day-offset arithmetic that made 23:00 read as tomorrow,
`"afternoon"` matching the daypart `"noon"`, `"what can you do"` matching a time hint because of the
substring `"at "`, `"on friday at 6pm"` becoming a permanent weekly alarm, `"1.5 hours"` parsing as
5 hours, `"every 30 minutes"` firing every minute. The commit message for Stage 3 lists them.

The same rule applies to the non-Kotlin checks. Building a real APK offline with `aapt2` rejected
`res/values/themes.xml`: three dotted styles (`Jarvis.Card`, `Jarvis.SectionTitle`, `Jarvis.Caption`)
declared no `parent`, and aapt2 resolves such a name as a child of the style named by its prefix, so
it demanded a style `Jarvis` that did not exist. That defect would have broken
`./gradlew :app:assembleDebug` and the CI workflow identically - the offline type-check could not see
it because `android.jar` says nothing about resource tables. The base style was added, and
`tools/validate_android.py` gained `check_style_hierarchy()`, so the rule is now enforced by
`./tools/verify_all.sh` rather than discovered by a build.

**Determinism everywhere.** `FixedClock` replaces the system clock, `FixedRandomBytes` replaces
secure random, and the in-memory stores replace SQLite. No test depends on the wall clock, on locale
formatting, or on a device.

## What is not covered — honestly

- **No instrumentation or UI tests yet.** Nothing exercises a real `Activity`, a real
  `AccessibilityService`, real TTS/ASR, or a real SQLite database. Those arrive with the stages that
  implement them (5–8), and will need an emulator in CI.
- **No end-to-end agent test.** The agent loop does not exist yet, so there is no test that a
  multi-step request actually completes on a screen.
- **No coverage measurement.** The suites are written test-first per stage, but no percentage is
  claimed and none is measured.
- **No fuzzing or property-based testing.** The NLU is tested against a hand-written corpus of
  sentences; adversarial input is not systematically explored.
- **`:app` coverage is thin by necessity.** Three checks, because the app is still a shell and its
  interesting behaviour is platform behaviour that cannot be verified offline.
- **The Android type-check is not a runtime guarantee.** Compiling against `android.jar` proves the
  API surface is used correctly at compile time. It cannot catch a `SecurityException` at runtime, a
  behavioural difference between API levels, or a permission denial on a specific OEM build.

## Adding a test

1. Put it in the suite for its subsystem, or create `object FooSuite : Suite("group/foo")`.
2. Register the suite in `AllSuites.all` (`:core`) or `AppAllSuites` (`:app`).
3. Run `./tools/local_core_check.sh` — it recompiles from scratch, so a stale class cannot pass.
4. If the test fails, decide which is wrong before changing either. The rule in this repo is that a
   failing test is a bug report first and a bad test second.
