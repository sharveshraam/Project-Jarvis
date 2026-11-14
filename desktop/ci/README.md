# CI workflow — manual step required

The GitHub App used by this session does not have the `workflows` permission,
so `.github/workflows/*` changes cannot be pushed automatically.

To enable the Windows build:
1. Copy `jarvis-desktop.yml` from this folder to `.github/workflows/jarvis-desktop.yml`
   in the repository (via GitHub web UI or a local clone with your own credentials).
2. The workflow runs on pushes to `main` / this branch, PRs, and manual dispatch.

The Android workflow (`.github/workflows/android.yml`) is unchanged.
