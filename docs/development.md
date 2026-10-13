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

The main window's **Show Logs** checkbox opens a resizable log panel below the
scanner. Filter by Info, Warnings, or Errors, turn auto-scroll on or off, copy
the visible messages, or clear the display. Any Error or Fatal message opens
the panel and selects Errors automatically. Clearing the display or starting
a new scan leaves the file logs intact. The panel keeps the most recent 2,000
entries (up to 500,000 characters); the files retain the full diagnostics.

Scan messages group character and inventory tasks with indented steps and
`[FAILURE]` details. Equipment OCR workers include item IDs because their
messages can arrive concurrently.

The Scan frame keeps the activity animation and adds a separate current-phase
progress bar. Equipment progress counts inspected cards against the game's
inventory total; the colored pills show confirmed results. Pending equipment
OCR jobs have their own progress when capture finishes. Characters count the
owned roster in a short name-only pass before reading full details. If that
count cannot be verified, the total stays `?` until a complete cycle is found.
Achievement progress counts categories that reached their verified bottom,
while its pill shows confirmed completed IDs against the game's global total.
Material totals remain `?` during discovery. Filter stops, failures and
cancellation preserve the actual measured progress.

The artifact lookup regression harness runs without game input:

```powershell
.\tests\run-artifact-database-tests.ps1 -BuildProject
```

It verifies complete set generation, the one-piece Prayer sets, and rejection
of missing or ambiguous source data. Optional `-SourceDirectory` and
`-CaptureDirectory` arguments replay saved 7.1 source files and the failed
artifact-name captures numbered 246–257. The harness builds a temporary lookup
in memory and leaves the application's cache and saved screenshots unchanged.

During scans, every misread item is dumped with its screenshots into
`logging/<weapons|artifacts|characters|materials>/`. These folders are the first place to look when
debugging recognition problems, and the most useful thing to attach to a bug report. Check the
**Log All Screenshots** option to force captures even for reads that succeeded.

Material quantity diagnostics include the material name, inventory section and
page, capture rectangle, preprocessing/OCR attempts, and the saved crop path.
Quantity OCR isolates the complete number from the rarity stars and rejects wide
card borders when locating the digit line. Failed reads are
retried with another capture and remain explicit failures; an unreadable
quantity is omitted from export instead of being recorded as zero. Visited
names are tracked separately so a failed quantity cannot prevent the material
scan from reaching its end.

After building, run `.\tests\run-material-quantity-tests.ps1` to check quantity
voting without opening the app. Optional `-CaptureDirectory` replays the five
saved quantity crops from this failure report, including Ethereal Crystal's
known quantity of 39. This replay expects those original values and sends no
input to the game.

Use `-ScreenshotPath <path> -ExpectedQuantity <count>` to replay a specific saved
failure without assuming the older captures still contain their original quantities.
The Frost Emperor's Revival failure crops both contain `7`.

After building, run `.\tests\run-traveler-tests.ps1 -SourceDirectory <saved dump directory>`
to verify all seven Traveler elements, source-order independence, Cryo's C3/C5 order,
custom-name matching and the `TravelerCryo` GOOD export. Optional `-HeaderDirectory`
replays the saved `unknown_layca.png` header and checks that other unknown Cryo names
remain unresolved. These checks use the built application without opening it, writing
lookup caches, or sending game input.

Character navigation verifies the menu, selected page and detail overlays before
clicking. UI waits retain a minimum duration at fast scan settings, and screen
transitions allow up to six fresh captures before failing. Entry and missed
pane/detail clicks have bounded retries based on the newly verified screen.
Each detail opens from its base pane so an unchanged previous talent or
constellation overlay cannot be mistaken for the next one.
Initial entry preserves the original sequence: close verified Paimon, verify
gameplay, select party slot one, wait for the switch, and press the configured
Character hotkey. Gameplay can be established by the health bar, two distinct
party key labels and an Enter/Space control; garbled HP digits do not block entry.
There is no second gameplay/HP check between the party switch and the Character
hotkey. The resulting Character menu must still be verified before scanning.
A dropped hotkey is retried only while gameplay remains positively recognized.
Escape closes a detail only after that overlay is recognized. A lost
menu triggers bounded recovery using the Character hotkey from verified gameplay
or the verified Character tile from Paimon, followed by restoration of the
interrupted roster position before a retry.
Recovery traversal does not increase scan progress or count as a completed cycle.
An unrecoverable menu stops further game input and saves partial inventory data.
The recovery budget is renewed only after a different character position has
been inspected, so independent interruptions do not exhaust a lifetime limit.
One character still has a bounded retry count. If its details remain unreadable
but the Character menu and exact roster checkpoint can be verified, the scanner
omits that character and continues. Skips are logged with the failed action and
evidence; the phase and final status remain partial even when the roster wraps.
Unknown screens and unverified checkpoints cannot authorize skipping or Next.
The optional roster count runs once. Its reads and final restoration share a
bounded recovery path: if counting or restoration fails, the scanner discards
the denominator and verifies the original character checkpoint before continuing
the actual scan. It does not restart the count. An unreadable initial header
causes no roster movement; an unverified recovery still stops input.

Constellation recognition uses the selected sidebar control and repeated ring
edges, including colored glowing medallions. It does not depend on their titles
or require every active ring to be white.
After three ordinary verification captures miss, the remaining bounded captures
can use a more tolerant detector for the expected character pane or overlay.
It requires two of three fresh frames to agree on the exact state, supported
by independent controls such as the selected tab and repeated pane elements.
One inconclusive frame may separate those matches; a positively conflicting
state clears them immediately. Sidebar detection uses multiple distinct control
labels, so one label obscured by the selected tab's animation does not block a
pane whose contents remain recognizable.
Confirmed gameplay, Paimon and other specific panes cannot be overridden. No
extra navigation input is sent during this second pass, and scanned character
values retain their normal validation. Logs include the strict result, relaxed
signals and confirmation count; successful fallback also saves its screenshot.

Navigation diagnostics include the action, detected screen, OCR/visual evidence,
recovery attempt and absolute screenshot path under `logging/characters/navigation`.
Talent OCR failures also save a full screenshot with the crop and last OCR text.
Run `.\tests\run-character-navigation-tests.ps1` for offline controller and
checkpoint checks. Optional `-CaptureDirectory` replays the saved Xiangling
constellation frame, Sigewinne's locked constellation with a long title, and
the gameplay header captured after the failure;
`-PaimonScreenshotPath` verifies a saved Paimon menu and its Character tile.
`-NavigationCaptureDirectory` replays all saved Paimon and failed gameplay frames
from the character-entry failure, including the HP bar interrupted by its own
text. These frames also drive the initial-entry controller through Escape,
party slot one and the configured Character hotkey using the actual recognizer.
`-PaneCaptureDirectory` replays the Yelan constellation failures through the
recognizer and pane-selection controller, with locked/open constellation details
as negative base-pane examples. Recovery tests inject navigation callbacks and
verify that an exhausted character retry can be omitted without exporting its
partial data or losing the next roster position.
`-CountCaptureDirectory` replays the saved roster-count interruption, its verified
Attributes entry and Paimon frames. The count checks also inject failures during
the count and final checkpoint restoration, verifying one recovery and no recount.

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
