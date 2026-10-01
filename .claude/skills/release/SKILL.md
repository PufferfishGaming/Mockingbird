---
description: Prepare a Mockingbird Studio release (version bump, release chain, commit, push, upload checklist). Manual only.
argument-hint: "<version> \"<one-line release notes>\""
disable-model-invocation: true
---

# Prepare release $ARGUMENTS

Follow `docs/RELEASE_PROCESS.md`. The first argument is the new version (for example `0.1.18`); the rest is the one-line note shown in the update banner of installed apps.

1. **Check the state.** `git status` must show no unexpected changes. Confirm the version is newer than the one in `Directory.Build.props`. A version that was already built is never rebuilt.
2. **Bump the version** in `Directory.Build.props` (`<Version>`) and in the `**Version x.y.z` line of `README.md`.
3. **Run the chain:** `scripts\release-chain.ps1 -Notes "<notes>"`. It runs verify, package, build-msi, build-setup, verify-package and release-audit, and stops at the first failure. Read the result: test totals (none failed, none skipped), the audit line, and the three file sizes and hashes it prints.
4. **Do not install the new build on this computer** unless the user asks. The point of a release is usually that the installed older version updates itself from GitHub.
5. **Commit** the version bump and any release-related changes (no Claude or co-author lines), then **push** as a normal push if the user asked for the release to be pushed.
6. **Give the user the upload list**, in this order, from `artifacts\packages\<version>\`: `Mockingbird-Studio-Setup.exe`, `SHA256SUMS.txt`, `latest.json` (last). Replace the same-named assets on the `download` release; wait for each upload to finish.
7. **After the user uploads**, verify on GitHub with fresh URLs (add a cache-busting query): asset names, sizes and SHA256 digests must equal the local files, and `latest.json` must be the last asset uploaded. Then confirm what an older app would be offered.

Never upload anything yourself, and never edit `latest.json` by hand: `build-setup.ps1` writes it with the real size and hash.
