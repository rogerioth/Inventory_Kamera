# Inventory Kamera documentation

This folder contains the technical documentation for Inventory Kamera — a Windows tool that scans a
Genshin Impact account via OCR and exports the result in the
[GOOD](https://frzyc.github.io/genshin-optimizer/good) JSON format.

## Contents

| Document | Contents |
| --- | --- |
| [Architecture](architecture.md) | Components, responsibilities, and how they fit together |
| [Scanning pipeline](scanning-pipeline.md) | End-to-end flow of a scan: window capture, input automation, OCR, and export |
| [Data updater](data-updater.md) | How lookup tables are generated from the AnimeGameData2 repository |
| [Lookup tables](lookup-tables.md) | Format reference for `inventorylists/*.json` (for manual edits) |
| [Project structure](project-structure.md) | What lives where in the repository and the Visual Studio solution |
| [Development](development.md) | Build prerequisites, working on the code, and debugging tips |

## Where to start

- **Using the scanner** — see the [README](../README.md) quick start.
- **Curious how it works** — read [Architecture](architecture.md), then
  [Scanning pipeline](scanning-pipeline.md).
- **Fixing a bad scan after a game update** — read [Data updater](data-updater.md) and
  [Lookup tables](lookup-tables.md).
- **Building or hacking on the code** — read [Development](development.md).

## Quick facts

- Language: C# / .NET Framework 4.7.2, WinForms UI
- OCR: Tesseract with a custom-trained `genshin` language model (see `InventoryKamera/tessdata`)
- Input automation: [WindowsInput (InputSimulator)](https://www.nuget.org/packages/InputSimulator/)
  plus Win32 `user32` interop
- Image processing: [Accord.NET](https://www.nuget.org/packages/Accord.Imaging/)
- Game data source: [Dimbreath/animegamedata2](https://gitlab.com/Dimbreath/animegamedata2) on GitLab
- Export format: [GOOD v2](https://frzyc.github.io/genshin-optimizer/good)
