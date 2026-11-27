# Architecture

Exile Lens is a .NET 10 / Avalonia desktop app. Everything that can be tested without a display lives below
the UI layer; the UI only binds view models to services.

```
ExileLens.App              Avalonia UI: theme tokens, main window, overlay window, tray, view models
ExileLens.Application      Use cases & abstractions: capture → parse → price / analyze, settings, profiles, history
ExileLens.Core             Pure domain: item parser, modifier normalization, map analysis, reward rules, pricing math
ExileLens.Infrastructure   HTTP: official trade API, trade data (leagues/stats), poe.ninja, GitHub updates, rate limiting
ExileLens.Game.Poe1/Poe2   Game modules: API segments, filter layouts, currency names, embedded rule data (JSON)
ExileLens.Platform.*       Global shortcuts, copy keystroke, window/cursor detection, auto-start per OS
tests/ExileLens.Tests      MSTest unit + integration tests with real clipboard fixtures for both games
benchmarks/                BenchmarkDotNet for parser, analyzer, disenchant and stat lookup
```

Dependencies point inward only: `App → Application/Infrastructure/Game/Platform → Core`.

## Main flows

**Ctrl + D (price check)**

1. The platform hotkey service sees the chord. It swallows the key only if a Path of Exile window is focused.
2. `GameActionCoordinator` resolves the action for the active game. Both games keep their own shortcuts.
3. `ItemCaptureService` sends exactly **one** copy keystroke (Ctrl+C, or Ctrl+Alt+C for advanced copy), then polls
   the clipboard for up to 600 ms. It restores the user's previous clipboard text only if the clipboard still
   holds the captured item, so a newer user copy is never overwritten.
4. `ItemTextParser` parses the text (≈6 µs).
5. The overlay opens straight away. Disenchant/salvage/vendor rewards are computed locally from cached rates.
6. `PriceCheckService` searches the market asynchronously. Identical concurrent queries share one request,
   results are cached for the configured time, and filter edits are debounced by 450 ms.

**Alt + D (map check)** works the same way up to parsing. It then runs `MapModifierAnalyzer` (≈6 µs) against the
active profile, records unseen modifiers so they can be classified, and shows the overlay. No network is needed.

## Decisions

| # | Decision | Why |
|---|----------|-----|
| 1 | **Modifier identity = normalized pattern** (`"+85(80-89) to maximum Life"` → `"# to maximum Life"`). Catalog ids are stable (`poe2.recovery`), and unknown modifiers get a deterministic `auto:<pattern>` id. | Profiles survive value rerolls and advanced-copy ranges. A modifier missing from the shipped catalog can still be classified, and it is never silently marked safe. |
| 2 | **Advanced copy by default.** | Affix headers give exact prefix/suffix counts, so a hybrid affix counts once. PoE 2 disenchant rules depend on that count (6 modifiers → 2 Regal Shards). When only plain copy is available, the count is an estimate and the result says so. |
| 3 | **Reward rules are versioned JSON** (`Game.*/Data/rewards.json`), with an optional user override in `<data>/rules/<game>/`. | Patch changes become a data edit, not a code change. Disenchant, salvage and vendor rules stay separate, so mutually exclusive rewards are never added together. |
| 4 | **Price estimate = inter-quantile range after removing outliers and duplicate sellers.** Listing prices are labelled as asking prices. | The cheapest listing is often bait. A range plus a confidence level is more honest than one number. When there is no data, the result says "unavailable" and no price is made up. |
| 5 | **Bulk items use poe.ninja exchange data**; equipment uses the official trade search. | One cached request per category instead of a trade search for every currency check, which keeps rate-limit use low. |
| 6 | **Client-side rate limiter learns limits from `X-Rate-Limit-*` headers** and honours `Retry-After`. Waits over 8 s fail fast with a clear message. | Limits are respected and never bypassed, and the overlay never hangs. |
| 7 | **One reused overlay window** with `ShowActivated=false`; content is swapped via DataTemplates. | Showing the overlay costs one layout pass, not a window creation, and it does not steal focus from the game. |
| 8 | **The Escape/close chord is registered only while an overlay is visible.** | The game's own Escape menu is never blocked. |
| 9 | **Settings use atomic writes with debouncing and a step-wise JSON migrator.** Corrupt files are moved aside and defaults are loaded. | A crash during a write can't corrupt settings, and old files keep loading. |
| 10 | **No background polling.** Currency rates refresh lazily when used (after 15 min). League and stat data load once at startup (stats are cached on disk for 24 h). The Overview status timer runs only while that page is visible. | Keeps idle CPU near zero. |
| 11 | **Source-generated JSON** for every persisted document; frozen dictionaries for lookups. | Startup and parse speed, low allocations. |

## Performance

Measured locally with a Release build (.NET 10, x64):

| Operation | Target | Measured |
|---|---|---|
| Item parsing (advanced rare, 7 lines + headers) | < 5 ms | ~6 µs |
| Map analysis (5 modifiers) | < 10 ms | ~6 µs |

Run `dotnet run -c Release --project benchmarks/ExileLens.Benchmarks` for the full BenchmarkDotNet suite.
Network time is reported separately (`PriceCheckResult.NetworkTime`).
