# Upstream issue resolution log

This fork continues [Andrewthe13th/Inventory_Kamera](https://github.com/Andrewthe13th/Inventory_Kamera),
which is no longer maintained. This page records how its open issues were addressed here, for
reference when comparing against the upstream tracker. Each entry links the original issue and the
pull request in this repository that resolved it.

## Accepted SonarCloud findings (10)

After the full cleanup sweep (PRs #21–#39), the SonarCloud issues tab is down to **10 findings — all
deliberate leaves** with a documented reason. Please do not "fix" these without reading the rationale;
if one must change, the contract it protects has to change first:

| Finding | Reason it stays |
| --- | --- |
| S107 on `Artifact(…)` / `Weapon(…)` constructors (10/8 params) | The signatures are part of the JSON round-trip and scanner capture paths; changing them risks deserialization and call-site stability for no functional gain. |
| S107 on `CharacterScraper.CountRosterBestEffort` / `AchievementRecognizer.ResolveSidebarRomanEvidence` | Internal helpers with multiple direct call sites in `tests/`; a parameter-object refactor would churn test contracts for a cosmetic rule. |
| S1144 ×4 on `Navigation.WindowPlacement` fields | The struct is marshaled to the Win32 `WINDOWPLACEMENT` layout (`Marshal.SizeOf` feeds its `length` field); removing "unused" fields corrupts the native layout. |
| S2325 on `GOOD.WriteToJson` / `WeaponScraper.CatalogueFromBitmapsAsync` | Compiler-verified: both call instance members (`ToString()` override / inherited instance method) and cannot be static. |

## Fixed in this fork

| Upstream issue | Fix | PR |
| --- | --- | --- |
| [#559](https://github.com/Andrewthe13th/Inventory_Kamera/issues/559), [#562](https://github.com/Andrewthe13th/Inventory_Kamera/issues/562), [#565](https://github.com/Andrewthe13th/Inventory_Kamera/issues/565), [#570](https://github.com/Andrewthe13th/Inventory_Kamera/issues/570), [#578](https://github.com/Andrewthe13th/Inventory_Kamera/issues/578), [#579](https://github.com/Andrewthe13th/Inventory_Kamera/issues/579), [#587](https://github.com/Andrewthe13th/Inventory_Kamera/issues/587), [#591](https://github.com/Andrewthe13th/Inventory_Kamera/issues/591) — artifact scan stops/freezes at Sanctifying Elixir artifacts | Banner detection + shifted crops; unreadable cards bounded-retry then skip; engine-pool leak and zero-blob crash fixed | [rogerioth/Inventory_Kamera#3](https://github.com/rogerioth/Inventory_Kamera/pull/3) |
| [#589](https://github.com/Andrewthe13th/Inventory_Kamera/issues/589), [#580](https://github.com/Andrewthe13th/Inventory_Kamera/issues/580), [#561](https://github.com/Andrewthe13th/Inventory_Kamera/issues/561), [#571](https://github.com/Andrewthe13th/Inventory_Kamera/issues/571), [#546](https://github.com/Andrewthe13th/Inventory_Kamera/issues/546), [#492](https://github.com/Andrewthe13th/Inventory_Kamera/issues/492) — unknown characters/weapons abort or hang the scan (Manekins, Pyro Traveler, new releases) | Skip-and-continue everywhere; bounded retries; per-item worker isolation | [rogerioth/Inventory_Kamera#2](https://github.com/rogerioth/Inventory_Kamera/pull/2) |
| [#583](https://github.com/Andrewthe13th/Inventory_Kamera/issues/583) — unactivated artifact substats | Captured and exported via an additive `unactivatedSubstats` field; level-100 characters accepted | [rogerioth/Inventory_Kamera#4](https://github.com/rogerioth/Inventory_Kamera/pull/4) |
| [#525](https://github.com/Andrewthe13th/Inventory_Kamera/issues/525), [#521](https://github.com/Andrewthe13th/Inventory_Kamera/issues/521) — row skipping / crashes on 16:10 resolutions | 16:10 grid geometry (40/page), rollback period, sanctified shifts | [rogerioth/Inventory_Kamera#5](https://github.com/rogerioth/Inventory_Kamera/pull/5) |
| [#511](https://github.com/Andrewthe13th/Inventory_Kamera/issues/511), [#576](https://github.com/Andrewthe13th/Inventory_Kamera/issues/576) — constellations that upgrade Normal Attack (Freminet, Sethos, Arlecchino, Lyney, Neuvillette, Wriothesley, Varesa) | C3/C5 targets detected per character; no more hardcoded burst adjustment | [rogerioth/Inventory_Kamera#6](https://github.com/rogerioth/Inventory_Kamera/pull/6) |
| [#596](https://github.com/Andrewthe13th/Inventory_Kamera/issues/596), [#557](https://github.com/Andrewthe13th/Inventory_Kamera/issues/557) — lookup table update fails / can't update | Atomic validate-then-replace saves; placeholder avatar rows skipped; inverted weapons-list check fixed | [rogerioth/Inventory_Kamera#7](https://github.com/rogerioth/Inventory_Kamera/pull/7) |
| [#482](https://github.com/Andrewthe13th/Inventory_Kamera/issues/482), [#515](https://github.com/Andrewthe13th/Inventory_Kamera/issues/515), [#535](https://github.com/Andrewthe13th/Inventory_Kamera/issues/535) — refinement misreads dropping weapons | Bounded multi-strategy OCR; weapons never dropped, fallback to refinement 1; engine whitelist leakage fixed | [rogerioth/Inventory_Kamera#8](https://github.com/rogerioth/Inventory_Kamera/pull/8) |
| [#327](https://github.com/Andrewthe13th/Inventory_Kamera/issues/327), [#433](https://github.com/Andrewthe13th/Inventory_Kamera/issues/433), [#505](https://github.com/Andrewthe13th/Inventory_Kamera/issues/505) — custom Traveler/Wanderer names crashing or never matching | Collision-safe matching (no `ArgumentException`), ASCII folding of custom names (Alægon works), collisions refused with a warning | [rogerioth/Inventory_Kamera#9](https://github.com/rogerioth/Inventory_Kamera/pull/9) |
| [#234](https://github.com/Andrewthe13th/Inventory_Kamera/issues/234), [#475](https://github.com/Andrewthe13th/Inventory_Kamera/issues/475) — GDI+ / "device is not ready" crashes | All diagnostic saves and captures non-fatal; output path validated up front | [rogerioth/Inventory_Kamera#10](https://github.com/rogerioth/Inventory_Kamera/pull/10) |
| [#530](https://github.com/Andrewthe13th/Inventory_Kamera/issues/530) — Enter key dead while scan-complete dialog is up | Global stop-hotkey released before any post-scan dialog | [rogerioth/Inventory_Kamera#11](https://github.com/rogerioth/Inventory_Kamera/pull/11) |
| [#536](https://github.com/Andrewthe13th/Inventory_Kamera/issues/536), [#366](https://github.com/Andrewthe13th/Inventory_Kamera/issues/366) — "Failed to initialize Tesseract engines" | Pre-flight with actionable fix-it steps, native-DLL presence logging, per-engine fallback, clean exit dialog | [rogerioth/Inventory_Kamera#12](https://github.com/rogerioth/Inventory_Kamera/pull/12) |

## Fixed by the data-source migration

The updater now generates lookup tables from
[Dimbreath/animegamedata2](https://gitlab.com/Dimbreath/animegamedata2) (see
[Data updater](data-updater.md), [rogerioth/Inventory_Kamera#1](https://github.com/rogerioth/Inventory_Kamera/pull/1)).
That alone resolved a family of "stale data" reports:

- [#555](https://github.com/Andrewthe13th/Inventory_Kamera/issues/555) — InventoryLists lacks Pyro Traveler (the regenerated Traveler entry includes pyro with per-element constellation orders)
- [#480](https://github.com/Andrewthe13th/Inventory_Kamera/issues/480), [#450](https://github.com/Andrewthe13th/Inventory_Kamera/issues/450), [#441](https://github.com/Andrewthe13th/Inventory_Kamera/issues/441), [#546](https://github.com/Andrewthe13th/Inventory_Kamera/issues/546) and similar "item/character X not scanned" reports from 2023–2024 — those items postdate the upstream tables; current tables include them (any residual misreads are OCR issues, not data issues)

## Not fixed (by design or out of scope)

- [#302](https://github.com/Andrewthe13th/Inventory_Kamera/issues/302) / [#517](https://github.com/Andrewthe13th/Inventory_Kamera/issues/517) (Seelie: Mora/XP books) — Mora and XP books export since the materials scanner landed; **weapon enhancement ores are intentionally not scanned** (they live in the weapons inventory tab and were designed out upstream)
- [#436](https://github.com/Andrewthe13th/Inventory_Kamera/issues/436) (non-English languages) — the OCR model is English-only; a multi-language model is a separate project
- [#516](https://github.com/Andrewthe13th/Inventory_Kamera/issues/516), [#568](https://github.com/Andrewthe13th/Inventory_Kamera/issues/568) (Linux/Wine), [#527](https://github.com/Andrewthe13th/Inventory_Kamera/issues/527) (GeForce Now) — the scanner requires a local window it can hook (Win32 + screen capture)
- [#40](https://github.com/Andrewthe13th/Inventory_Kamera/issues/40) (ultrawide), [#164](https://github.com/Andrewthe13th/Inventory_Kamera/issues/164) (HDR) — documented limitations (see the [README](../README.md))
- [#227](https://github.com/Andrewthe13th/Inventory_Kamera/issues/227) (all characters scanned as C6 at some resolutions) — constellation-click geometry needs live-game repro; not addressed
- [#474](https://github.com/Andrewthe13th/Inventory_Kamera/issues/474) (first row of 3★ weapons skipped on non-16:9) — needs field-captured geometry; the 16:10 artifact fix ([PR #5](https://github.com/rogerioth/Inventory_Kamera/pull/5)) covers the artifact side only

## Acknowledgments

Several fixes are ports from [taiwenlee/Inventory_Kamera](https://github.com/taiwenlee/Inventory_Kamera)
(MIT), whose commits are referenced in-code; the rest were diagnosed fresh against this codebase.
