# Project structure

Inventory Kamera is a Visual Studio 2022 solution targeting .NET Framework 4.7.2.

```
Inventory_Kamera/
├── README.md
├── docs/                        ← this documentation
├── InventoryKamera.sln          ← Visual Studio solution
├── InventoryKamera/             ← the scanner application (see below)
└── Inventory Kamera Installer/  ← Visual Studio setup project (.vdproj) for releases
```

## The `InventoryKamera` project

```
InventoryKamera/
├── Program.cs                   ← entry point: logging setup, DPI awareness, MainForm
├── Settings.cs
├── App.config                   ← app settings + assembly binding redirects
│
├── ui/                          ← WinForms layer
│   ├── main/
│   │   ├── MainForm.cs          ← scan controls, options, updater prompt, export
│   │   └── MainUI.cs            ← earlier UI iteration (unused)
│   ├── ExecutablesForm.cs       ← keybind configuration dialog
│   └── UserInterface.cs         ← static helpers for thread-safe UI updates
│
├── scraping/                    ← game menu walkers
│   ├── InventoryScraper.cs      ← base class: paging, capture, retries
│   ├── WeaponScraper.cs
│   ├── ArtifactScraper.cs
│   ├── CharacterScraper.cs
│   ├── MaterialScraper.cs
│   └── GenshinProcesor.cs       ← OCR post-processing, lookup tables, name conversion
│
├── game/                        ← game-facing primitives
│   ├── Navigation.cs            ← window discovery, input, screenshots (Win32 + InputSimulator)
│   ├── Character.cs
│   ├── Weapon.cs                ← includes the WeaponType enum
│   └── Artifact.cs
│
├── data/                        ← scanning core + game data
│   ├── InventoryKamera.cs       ← scan coordinator: scraper sequence + OCR worker pool
│   ├── Inventory.cs             ← in-memory inventory model
│   ├── OCRImageCollection.cs    ← cropped screenshots per item, passed to OCR workers
│   ├── Rect.cs                  ← Win32 rectangle helpers
│   ├── DatabaseManager.cs       ← updater: AnimeGameData2 → inventorylists/*.json
│   └── GOOD.cs                  ← GOOD v2 export serializer
│
├── tessdata/                    ← Tesseract language models
│   ├── genshin_fast_09_04_21.traineddata   ← active model
│   └── genshin_best_eng.traineddata        ← experimental
│
└── Properties/                  ← assembly info, resx, user settings
```

## Key runtime artifacts (not in the repo)

| Path | Created by | Purpose |
| --- | --- | --- |
| `inventorylists/*.json`, `version.txt` | `DatabaseManager` | Lookup tables + game data version |
| `logging/` | scan + NLog | Category screenshots for misreads, `InventoryKamera.log`, `InventoryKamera.debug.log`, archives |
| Export folder (user-configured) | `GOOD` | Scanned data as GOOD v2 JSON |

## Solution notes

- The solution contains the app project and the installer (`.vdproj`, buildable only inside Visual
  Studio, used for release packaging).
- A WPF UI experiment exists upstream but was intentionally never committed (`/InventoryKameraWPF/`
  is in `.gitignore`) and is not part of the solution.
- NuGet dependencies are declared with `PackageReference` in the `.csproj`; restore happens
  automatically on build in Visual Studio, or with `msbuild -t:restore`.

Main dependency list: `Accord.Imaging` (image filters), `Tesseract` (OCR), `InputSimulator`
(keyboard/mouse), `Newtonsoft.Json` (serialization), `NLog` (logging), `HtmlAgilityPack`
(scraping helpers), `WindowsAPICodePack` (shell dialogs), `NHotkey` (global hotkeys).
