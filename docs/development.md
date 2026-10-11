# Development

How to build Inventory Kamera from source and work on it comfortably.

## Prerequisites

- **Visual Studio 2022** (Community edition is fine) with the
  **.NET desktop development** workload. The classic installer-project support workload is only
  needed if you want to build `Inventory Kamera Installer.vdproj`.
- **.NET Framework 4.7.2 targeting pack** (offered by the VS installer workload above).
- Windows 10/11. The app uses WinForms, Win32 interop, and per-monitor DPI calls — building or
  running on other platforms is not supported.

## Building

Clone, then either:

- Open `InventoryKamera.sln` in Visual Studio, restore happens automatically, press `F5`; or
- Command line:

  ```bat
  msbuild InventoryKamera.sln -t:restore -p:Configuration=Debug -p:Platform="Any CPU"
  msbuild InventoryKamera.sln -p:Configuration=Debug -p:Platform="Any CPU"
  ```

  (`msbuild` from a *Developer Command Prompt for VS 2022*.)

The first run of the built app generates `inventorylists/` by pulling game data from
[animegamedata2](https://gitlab.com/Dimbreath/animegamedata2) — an internet connection is required
and the first text-map download is tens of megabytes.

## Running a scan while debugging

The scanner needs a live Genshin Impact window. Practical setup:

1. Run the game (windowed, 16:9/16:10, English) and stay on the Paimon menu.
2. Launch the app **from Visual Studio** (`F5`) so breakpoints land in scanner/OCR threads.
3. Breakpoints inside `ImageProcessorWorker` or scraper loops will freeze input automation while
   hit — stop the scan (`Enter`) before stepping for long, or the game menu may drift.

## Logging

NLog is configured in `Program.ConfigureLogging()`:

- `logging/InventoryKamera.log` — info and above
- `logging/InventoryKamera.debug.log` — everything
- `logging/archives/` — rotated archives (4 kept)

During scans, every misread item is dumped with its screenshots into
`logging/<weapons|artifacts|characters|materials>/`. These folders are the first place to look when
debugging recognition problems, and the most useful thing to attach to a bug report. Check the
**Log All Screenshots** option to force captures even for reads that succeeded.

## Where to make common changes

| Change | Location |
| --- | --- |
| New game version breaks lookups | Usually nothing — the updater regenerates `inventorylists/`. If parsing breaks: `data/DatabaseManager.cs` |
| OCR reads the wrong region | Region crops in `scraping/*Scraper.cs` |
| A stat/element name maps wrong | `scraping/GenshinProcesor.cs` (`Stats`, `Elements` dictionaries) |
| Add a menu/screen the scanner can walk | Subclass `scraping/InventoryScraper.cs` |
| Export format drift | `data/GOOD.cs` |
| Keybind handling | `game/Navigation.cs`, `ui/ExecutablesForm.cs` |
| Game data upstream moves again | URLs in `data/DatabaseManager.cs` and [docs/data-updater.md](data-updater.md) |

## Conventions

- C# with 4-space indentation; the codebase mixes tabs/spaces between files — match the file you
  are editing.
- Logging via NLog (`Logger.Info/Debug/Warn/Error`); user-facing errors go through
  `MessageBox` on the UI thread.
- OCR-facing names flow through `GenshinProcesor` GOOD-normalization — never compare raw OCR text
  against literal names.
- Keep scrapers input-only: they navigate and capture, they do not OCR (see
  [Scanning pipeline](scanning-pipeline.md)).

## Upstream

This is a community continuation of
[Andrewthe13th/Inventory_Kamera](https://github.com/Andrewthe13th/Inventory_Kamera). When fixing
scanner bugs, the upstream issue tracker can be a useful archive of known problems and their
diagnoses.
