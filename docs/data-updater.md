# Data updater

Inventory Kamera never hardcodes item names. Every name, set piece, element, constellation order,
and weapon type it accepts comes from four lookup tables in `inventorylists/` that are generated
from the game's raw data dumps. This document explains where that data comes from and how the
updater works. For the table file formats, see [Lookup tables](lookup-tables.md).

## Data source

All game data comes from **[Dimbreath/animegamedata2](https://gitlab.com/Dimbreath/animegamedata2)**,
a GitLab repository of extracted Genshin Impact release data (the successor to the original
`AnimeGameData` repository, which is no longer updated). Two parts of that repository are used:

| Path | Used for |
| --- | --- |
| `TextMap/TextMap_MediumEN.json` | Hash → English text mapping for every localized string |
| `ExcelBinOutput/*.json` | Structured game tables: avatars, weapons, relics, materials, talents, skills |

> **Why `TextMap_MediumEN.json` and not `TextMapEN.json`?** In the newer dumps the full
> `TextMapEN.json` is a much larger corpus whose keys no longer align one-to-one with the
> `nameTextMapHash` values referenced in `ExcelBinOutput`. The "Medium" text map is the one that
> resolves the hashes used by the excel tables.

## Version detection

The updater needs to know which game version the local tables were generated for. Commit titles in
`animegamedata2` follow the pattern `CNRELWin<major>.<minor>.<patch>_<build hashes>`
(e.g. `CNRELWin7.1.0_R48379043_S48511369_D48533839`), so the version is parsed from the commit
title with the regex `\d+\.\d+\.\d+`.

The latest commits are read from the repository's **Atom commit feed**
(`https://gitlab.com/Dimbreath/animegamedata2/-/commits/main.atom`) rather than the GitLab REST API,
because the API endpoint is protected by bot-detection that rejects non-browser clients. Raw file
downloads (`/-/raw/main/...`) are unaffected.

The detected version is stored in `inventorylists/version.txt` and compared on every startup.

## Update flow

```mermaid
flowchart TB
    A["App starts<br/>(or Options → Update Lookup Tables)"] --> B{"Remote version<br/>> local version.txt?"}
    B -->|"no"| C{"Artifact generator revision<br/>and table hash current?"}
    C -->|"yes"| K["Use existing tables"]
    C -->|"no"| L["Download mappings and artifact sources<br/>Validate and repair artifact table"]
    L --> M["Write generator revision and table hash<br/>Reload lookup tables"]
    B -->|"yes"| D["Offer update"]
    D --> E["UpdateGameData()"]
    E --> F["Download TextMap_MediumEN.json<br/>(shared hash → name mappings)"]
    F --> G1["weapons.json<br/>from WeaponExcelConfigData"]
    F --> G2["artifacts.json<br/>from ReliquarySet + EquipAffix<br/>+ Reliquary configs"]
    F --> G3["characters.json<br/>from Avatar + FetterInfo<br/>+ Talent + Skill configs"]
    F --> G4["materials.json<br/>from MaterialExcelConfigData"]
    G1 & G2 & G3 & G4 --> H{"All lists OK?"}
    H -->|"yes"| I["Write new version to version.txt"]
    H -->|"any list failed"| J["Keep old version.txt;<br/>error surfaced to UI/log"]
```

Notes on each list:

- **Weapons** — every weapon whose name hash resolves in the text map. Unreleased weapons (hash not
  in the text map) are skipped with a warning; this is expected.
- **Artifacts** — enabled `ReliquarySet` rows provide the set IDs and representative piece IDs.
  `EquipAffix` supplies the set names, while `Reliquary` supplies each piece's name and slot.
  Generation checks every enabled set, its piece IDs, and all expected slots before replacing
  the table. The four one-piece Prayer sets intentionally contain only a circlet.
- **Characters** — for each `AVATAR_FORMAL` avatar: display name, element (from fetter info),
  constellation name, constellation C3 order (`skill` first or `burst` first, inferred by checking
  which talent the constellation description mentions), and weapon type. The Traveler is special-cased:
  one entry covering all seven elements with per-element constellation orders. Its available
  skill depots identify the elemental skill and burst IDs and the C3/C5 talent IDs; those
  IDs are joined to the descriptions instead of relying on icon names or dump row order.
- **Materials** — the material categories the scanner reads: EXP fruit, avatar materials, exchange
  goods, wood, fish bait, and weapon enhancement stones.

## Known quirks in current dumps

These are handled by the updater but worth knowing about:

- **Some JSON keys are omitted entirely** (rather than `null`) in the newer `ExcelBinOutput` dumps —
  e.g. `skillIcon` on some skill rows. Lookups are written null-safe for this reason.
- **`WEAPON_CROSSBOW` exists only as placeholder data** (one unnamed template weapon; the Traveler
  rows are tagged with it). There is no crossbow class in-game, so the updater treats it as
  **sword** — the Traveler's actual weapon — until a real crossbow class ships. If a future game
  version adds one, `Weapon.cs` and the updater's weapon-type mapping need a new case.
- **Test avatars** (`Mannequin`/`Manekina`) are flagged `AVATAR_FORMAL` in the dumps but have no
  talents, so they fail generation and are skipped — which is the correct outcome.
- **`TextMapEN.json`** in the new repo does **not** resolve the excel hashes (see note above);
  using it yields empty results.
- **Artifact display-item hashes can be absent from the text map.** The old display-item join
  omitted Scarlet Proof and Heart of the Furnace even when `version.txt` was marked `7.1.0`.
  The set/affix join resolves both sets and all their pieces.

## Artifact cache repair

`artifacts.metadata.json` records the artifact generator revision and the table's SHA-256 hash.
An old cache without that metadata, or a changed table whose hash no longer matches, triggers
an automatic artifact-only rebuild at startup. Artifact scans check again before controlling
the game, so a failed startup repair is retried instead of scanning with an obsolete table.
This repair does not require a newer game version or a force-update confirmation.

The updater builds and validates the complete table in memory, then replaces the file atomically.
Missing names, pieces, or slots fail the update and preserve the previous table. The current
7.1 dump produces 63 enabled sets, including both previously missing sets.

## Traveler cache repair

`traveler.metadata.json` records the Traveler generator revision and an SHA-256 hash of
the Traveler entry. Older caches, including caches already marked `7.1`, are rebuilt at
startup and checked again before a character scan. The repair replaces only Traveler's
entry and preserves the other characters and `version.txt`.

Cryo uses skill depots `505`/`705`, elemental skill `10127` and burst `10128`. Its C3
increases the burst and C5 increases the skill, so the scanner subtracts the respective
three-level constellation bonuses before GOOD export. Custom names still resolve to
the base Traveler entry; the Cryo form exports as `TravelerCryo`.

Incomplete skill, talent or element data rejects generation and preserves the existing
character table. A failed repair blocks the character scan with an update error instead
of silently skipping unsupported Traveler forms.

## Manual updates

The updater covers normal operation. If it fails (network error, upstream format change), you can
edit the JSON files by hand — the formats are documented in
[Lookup tables](lookup-tables.md). Use **force update** to regenerate the tables. Manual changes
to the artifact table invalidate its metadata hash and are repaired on the next startup or
artifact scan.
