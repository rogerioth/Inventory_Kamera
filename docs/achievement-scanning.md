# Achievement scanning

Achievement scanning reads the achievement list on screen with OCR. It opens
the Achievements button in the Paimon menu, visits the categories in the left
sidebar, and scrolls each category's achievement list. It never types achievement
names into the search box.

## Running a scan

1. Run Genshin Impact in English, using a visible 16:9 or 16:10 game window.
2. Open the Paimon menu and enable **Achievements** in Inventory Kamera's scan
   items. Disable the other scan items to scan achievements alone.
3. Choose a writable output folder and start the scan. Leave the mouse and
   keyboard alone while the scanner navigates.
4. Press **Enter** to stop. A stopped or incomplete achievement scan retains its
   confirmed results for export, with its incomplete status in the report.

The scanner reads category names, achievement titles and descriptions, rewards,
progress, completion marks, dates, and completed totals where visible. It uses a
cached achievement catalog to resolve the text to numeric game achievement IDs.
During scanning, **Preview** shows each complete achievement row as a wide image
with its recognized title, description, progress or completion status underneath.
Only fully visible achievement cards are read and previewed; fixed category
progress panels and clipped cards are excluded. Unresolved cards also appear,
with the reason they need review. The usual equipment
and character preview layout returns when starting the next scan.
The catalog is downloaded from Paimon.moe's English achievement data and cached
under `inventorylists/achievements.json`. The English OCR model ships with the application; scanning does not
require a separate OCR installation.

Use **Options → Update Achievement Catalog** to refresh achievement data. The
existing **Update Lookup Tables** action updates the inventory catalogs separately.

## Reading rows and achievement tiers

The right panel is captured as a viewport. The usable list area adapts to fixed
category progress panels, which are not achievement cards. Only fully visible
cards inside that area are read. Adjacent captures overlap, allowing cards that
were clipped at the bottom of one capture to be read fully in the next capture.
Scrolling advances most of the usable viewport and adapts to measured movement,
keeping a full row or two as overlap. If a jump loses that overlap, the scanner
backs up and reduces it.
Unchanged, successfully read rows are reused from the previous capture; new,
changed, and unresolved rows go through OCR. Overlapping captures must not add
the same completed achievement twice.

Achievement titles are not unique. The resolver considers the category, title,
description, reward, and numeric goals before assigning a family and tier. A
visible later tier can imply completed earlier tiers in the same verified
family. For example, a completed tier for a target of 100 can include its catalog
predecessors; a visible incomplete tier for a target of 100 cannot itself be
exported as completed. The report distinguishes visible observations from the
completed IDs derived from them.

Short-title OCR confusion between `0` and `O` is repaired only when strong
description, reward, and numeric evidence support the same catalog achievement.
Ambiguous repairs remain unresolved.

An ambiguous or unreadable row is retained as unresolved evidence. The scanner
does not pick an arbitrary achievement merely because its title is similar.
Hidden, unobserved, or unknown achievements are not reported as incomplete.

## Outputs

Achievement exports are separate from the existing equipment/material GOOD
export. The achievement export includes these compatible JSON shapes, following
the formats used by AkashaScanner:

| File format | Shape | Contents |
| --- | --- | --- |
| Akasha native | `{"Version":1,"Data":{"1001":1}}` | Completed achievement ID to category ID |
| Paimon.moe | `{"achievement":{"1":{"1001":true}}}` | Completed IDs grouped by category |
| Seelie | `{"achievements":{"1001":{"done":true}}}` | Completed ID to completion flag |
| Inventory Kamera report | Scan metadata, observations, coverage, issues and confirmed completed IDs | Progress, dates, unresolved evidence and completion status |

The IDs in the examples are illustrative. Compatible exports contain confirmed
completed IDs only. The native/Paimon/Seelie schemas cannot preserve OCR
confidence, completion dates, numeric progress or scan completeness, so keep the
Inventory Kamera report when investigating a scan.

An incomplete scan's output filenames contain `partial`. This matters when
importing into a tracker: an incomplete file describes the confirmed results
captured so far, and its filename/report must be considered before using it to
replace existing tracker data. A valid scan with zero completed achievements
exports empty achievement objects rather than inventing entries.

## Completeness checks

A scan is complete only after the scanner reaches the bottom of every catalog
category, resolves its rows without outstanding issues, and reconciles the
distinct completed IDs with the global **Achievements completed** counter.
The global total is read before and after traversal and must remain unchanged.

Each category selection uses the initially recognized, fully visible tile for
its first click and verifies the selected category before reading achievements.
Unmatched sidebar names are retried with single-line OCR and alternative image
thresholds. Selection attempts are bounded; a failed category does not stop the
remaining categories. One final recovery pass resets the sidebar and retries
missed categories. Any category still unverified leaves the result incomplete.

The scanner checks actual row movement while scrolling. A repeated capture
alone is not enough to prove the bottom was reached: wrong scroll focus, dropped
input, and an unfinished animation can also produce repeated captures. An
uncertain end, missing category, stale catalog, unresolved row, cancellation,
or count mismatch leaves the result incomplete.

## Troubleshooting

- **The scan cannot enter Achievements:** start with the Paimon menu open and
  verify the game window is visible. Menu recognition is used to avoid clicking
  the wrong screen.
- **Rows are unresolved or the count differs:** inspect the report and
  `logging/achievements`, then use **Options → Update Achievement Catalog** if the game recently
  changed. A new achievement may be absent from an older cached catalog.
- **Scrolling stalls:** verify the game still has focus and the mouse is not
  being moved during the scan. The scan must stop or report incomplete coverage
  instead of silently treating a stalled middle page as the end.
- **OCR reads digits or punctuation incorrectly:** use a supported aspect ratio,
  disable HDR, and retain the row screenshot with its raw OCR for diagnosis.
  Descriptions and goal numbers help distinguish tiers with identical titles.
- **Exports are marked partial:** inspect issues and category coverage in the
  report. The exports still contain the IDs confidently recognized so far.

## Regression checks

The regression harness is a console program compiled from the application source
using the installed Visual Studio C# compiler and the .NET Framework 4.7.2
targeting pack. It uses the application's restored dependencies and adds no
test-framework packages. It does not send keyboard or mouse input to the game.

From the repository root, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\run-achievement-tests.ps1 -BuildProject
```

Once the app has been built, omit `-BuildProject` to reuse its dependencies. You
can supply `-DependencyDirectory` or `-CompilerPath` if they are installed in
different locations. Generated executables and copied OCR dependencies stay in
the ignored `artifacts/achievement-tests` directory.

The optional screenshot replay uses the supplied reference screenshot of the
Wonders of the World category. It expects six complete rows and rejects the
partially visible seventh row; it also checks the header's total of 1605 and
the displayed rewards, progress and completion evidence:

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\run-achievement-tests.ps1 `
    -ScreenshotPath 'C:\path\to\achievement-reference.png'
```

Use the optional `-CaptureDirectory` flag to replay saved scan captures. This
checks fixed progress headers, clipped rows, sidebar recognition, and scrollbar
top and bottom detection without controlling the game:

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\run-achievement-tests.ps1 `
    -CaptureDirectory '.\InventoryKamera\bin\x64\Debug\logging\achievements'
```

The screenshot is supplied locally and is not copied into the repository. A
passing replay validates recognition on that image; it does not establish that
live navigation or every supported resolution has been tested. Live game
verification should include independent sidebar/right-panel scroll focus,
category changes, top and bottom detection, stopping, and both supported aspect
ratios.
