# Implementation status

How to read this page: **verified** means compiled with warnings-as-errors and covered by passing tests.
**Written, not yet compiled** means complete code that has not been built yet; CI is the first build.

| Area | Status |
|---|---|
| Core: parser (PoE 1/2, plain and advanced copy, hybrid affixes, unknown sections), normalization, map analyzer (levels, thresholds, combinations, warning escalation), reward rules, listing estimator, currency conversion, trade query builder, stat index | Verified — 99 tests |
| Application: settings (atomic writes, migration, quarantine), profiles (CRUD, import/export, discovered modifiers), history, capture/clipboard restore, coordinator (per-game shortcuts, conflict detection), price check service (dedup, cache, cancellation) | Verified |
| Infrastructure: trade client and parsers, trade data cache, rate limiter, poe.ninja provider, GitHub updater | Compiled; parsing, rate limiting and asset selection covered by tests. Live endpoints were not called during development |
| Platform: Win32 hook / SendInput, X11 grabs / XTest, Quartz event tap | Compiled (P/Invoke); needs manual testing on each OS |
| Avalonia app: theme tokens, main window and pages, overlay window, overlays matching the reference mockups, tray | Written, not yet compiled: Avalonia packages were not available in the development sandbox. CI builds it on all three OSes |
| CI/CD: PR validation, release on `release` branch, installers, AppImage, DMG, checksums | Written; runs on GitHub Actions |

## Known follow-ups

- Run the app on Windows, X11, Wayland/XWayland (KDE, GNOME, Hyprland) and macOS, and record the results in
  `PLATFORM-SUPPORT.md`.
- Extend the PoE 2 `map-modifiers.json` with the full waystone modifier list of the current league (PoE 1 is
  synced from poe.re). Missing modifiers are already recorded automatically as they are seen.
- Translations: only English UI strings exist today. The Language setting is in place for later locales.
