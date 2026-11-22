# Eel-ditor — notes for Claude Code

Read `docs/DESIGN.md` before designing a feature: it holds the principles and past decisions (clean git history
first, display-only rendering, no mixed modes, AltGr always types, rejected ideas). Keep it up to date: when a
decision is made or changed, add it there with the reason.

## Working rules
- The user writes in Polish; answer in Polish. Code, comments, commit messages and docs are in English.
- Do not commit unless asked. Do not push.
- All UI text goes through `Loc`; add new keys to `Lang/pl.json`, `en.json` and `isv.json` (use the isv-translator skill).
- Every bug fix gets a regression test.

## Build and test
- The user usually has the app running from `bin/EelDitor.App/Debug/net10.0` (output is in `bin/` and `obj/` next to `src`,
  set in `Directory.Build.props`), which locks it. Never kill it; build tests
  into a separate artifacts folder:
  `dotnet test tests/EelDitor.App.Tests --artifacts-path <scratch>/testart` (same for Core and Markdown tests).
- Headless tests: `using Avalonia.Headless;` for `KeyPress`/`MouseDown`; never `Close()` a window with a dirty
  document (a modal save prompt hangs the test).
- Tests must use `SettingsStore.UseDirectory(TestDirs.New())`.
- Random-key explorer: `EELDITOR_EXPLORE=1`.
- Installer: `.\build-installer.ps1` (Inno Setup 6).
- Python: run as `py`.
