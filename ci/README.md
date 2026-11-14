# Continuous integration

`github-actions.yml` in this directory is a complete, ready-to-use GitHub Actions
workflow. It lives here rather than in `.github/workflows/` for one specific reason:

> GitHub refuses pushes that create or modify `.github/workflows/*` from an app token
> that lacks the `workflows` permission:
>
> ```
> ! [remote rejected] (refusing to allow a GitHub App to create or update workflow
>   `.github/workflows/ci.yml` without `workflows` permission)
> ```
>
> The automated development token used on this repository does not have that permission,
> so the workflow is committed here instead of being silently dropped. Nothing about the
> workflow is provisional - it just needs to be moved into place by a token (or a person)
> that is allowed to touch it.

## Enable it

```bash
mkdir -p .github/workflows
cp ci/github-actions.yml .github/workflows/ci.yml
git add .github/workflows/ci.yml
git commit -m "ci: enable GitHub Actions"
git push
```

Or, in the GitHub UI: **Actions → New workflow → set up a workflow yourself**, and paste
the contents of `ci/github-actions.yml`.

Alternatively, grant the app the `workflows` permission (**Settings → Actions →
General**, or the app's repository permissions) and the file can be committed directly to
`.github/workflows/`.

## What it runs

### Job 1 — `offline` (no Android SDK, ~2 minutes)

Enforces the two architectural claims the whole project rests on:

| Step | Proves |
| --- | --- |
| `tools/local_core_check.sh` | `:core` has zero dependencies; its entire behavioural suite runs with only `kotlinc` + a JRE |
| `tools/validate_android.py` | Every `@type/name`, `R.type.name`, manifest component class and view id resolves |
| `tools/local_android_check.sh` | `:app` has zero third-party dependencies and type-checks against the real `android.jar` |

This job fails fast and cheaply. If it is red, the foundation is broken and there is no
point spending twenty minutes on a Gradle build.

### Job 2 — `android` (real SDK)

`./gradlew :core:test :app:assembleDebug :app:testDebugUnitTest :app:lintDebug`, then
uploads the debug APK and all reports as artifacts.

## Verification without CI

Every check in job 1 runs locally with no network beyond the one-time tool downloads:

```bash
./tools/verify_all.sh
```

See `docs/TESTING.md` for what each layer does and does not prove.
