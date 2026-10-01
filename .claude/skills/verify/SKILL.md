---
description: Build, run every test and the app smoke test, then summarize the result. Use before committing.
---

# Verify the repository

Run `scripts\verify.ps1` from the repository root (build with a locked restore, all tests, then the application smoke test).

Report, in a few lines:
- whether the build had zero warnings and zero errors;
- the number of tests passed, failed and skipped per test assembly and in total (the baseline is documented in `CLAUDE.md`);
- whether the smoke test succeeded (it reports `success`, `windowCreated` and the shell matrix count).

If anything failed, show the first failing test name and its message, not the whole log. Do not change code in this command.
