# Inventory Kamera

A fan-made Genshin Impact inventory scanner for Windows. Inventory Kamera navigates the game's menus,
reads your characters, weapons, artifacts, and materials with OCR, and exports everything in the
[GOOD](https://frzyc.github.io/genshin-optimizer/good) JSON format.

The exported file works out of the box with popular tools such as
[Genshin Optimizer](https://frzyc.github.io/genshin-optimizer/#/),
[SEELIE.me](https://seelie.me/), and [Aspirine's Genshin Impact Calculator](https://genshin.aspirine.su/).

> This project is a community continuation of [Andrewthe13th's Inventory Kamera](https://github.com/Andrewthe13th/Inventory_Kamera),
> which is no longer maintained. Game data is sourced from
> [Dimbreath's AnimeGameData2](https://gitlab.com/Dimbreath/animegamedata2).

## Download

Grab the latest release from the
[releases page](https://github.com/rogerioth/Inventory_Kamera/releases/latest) and unzip it anywhere
you like, then run `InventoryKamera.exe`. Windows may show a SmartScreen prompt — this happens because
the binary is not code-signed; the access is what lets the scanner interact with the game window.

## Requirements

| Requirement | Details |
| --- | --- |
| Operating system | Windows 10 or 11 |
| Game | Genshin Impact (global or CN client) |
| Game language | English |
| Aspect ratio | 16:9 or 16:10, windowed or borderless (fullscreen not supported) |
| Runtime | [Visual C++ Redistributable 2015–2022](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist#visual-studio-2015-2017-2019-and-2022) — a restart may be needed after installing |

The game must also meet these settings before scanning:

- **Game Language** set to *English* (`Settings → Languages`).
- **Control Type** set to *Keyboard* (`Settings → Controls`). If you rebound the inventory (`B`),
  character (`C`), or slot-1 (`1`) keys, either restore the defaults or change the keybinds in
  Inventory Kamera's options.
- HDR disabled in Windows settings if you experience scanning issues.
- Ultrawide monitors are not supported (see the
  [original explanation](https://github.com/Andrewthe13th/Inventory_Kamera/issues/40)).

## Quick start

1. Launch Genshin Impact and log in.
2. Start Inventory Kamera and pick what to scan (weapons, artifacts, characters, materials),
   plus any rarity/level filters, in the scanner options.
3. Leave the game sitting on the **Paimon menu** and click **Scan**.
4. Do not touch the mouse or keyboard while the scan runs. Press `Enter` at any time to cancel;
   you can then use **Export Scanned Data** to save what was collected so far.
5. When the scan finishes, use **Open Export Folder** to find your `.json` file and import it
   into your tool of choice.

Inventory Kamera only reads the screen and sends menu-navigation keystrokes — it never modifies the
game. See [docs/scanning-pipeline.md](docs/scanning-pipeline.md) for how that works under the hood.

## Keeping data current

Inventory Kamera validates everything it reads against per-version lookup tables in the
`inventorylists` folder. The scanner checks for a new game data version on startup and offers to
update automatically; you can also run **Options → Update Lookup Tables** manually at any time.

- Details on the automatic updater: [docs/data-updater.md](docs/data-updater.md)
- Editing the tables by hand: [docs/lookup-tables.md](docs/lookup-tables.md)

## Documentation

| Document | Contents |
| --- | --- |
| [docs/index.md](docs/index.md) | Documentation index |
| [docs/architecture.md](docs/architecture.md) | High-level architecture and component diagram |
| [docs/scanning-pipeline.md](docs/scanning-pipeline.md) | How a scan works: navigation, OCR, and export |
| [docs/data-updater.md](docs/data-updater.md) | The game data updater and its sources |
| [docs/lookup-tables.md](docs/lookup-tables.md) | Lookup table formats for manual editing |
| [docs/project-structure.md](docs/project-structure.md) | Repository and solution layout |
| [docs/development.md](docs/development.md) | Building, debugging, and contributing |

## Reporting issues

Please [open an issue](https://github.com/rogerioth/Inventory_Kamera/issues/new/choose) and check for
existing reports first. Scans that go wrong leave categorized screenshots in the `logging` folder —
attaching a zip of that folder is the single most useful thing you can include.

## Contributing

Pull requests are welcome. See [docs/development.md](docs/development.md) for build instructions and
a tour of the codebase.

## FAQ

**Can Inventory Kamera get me banned?**
Per [HoYoverse's statement on third-party software](https://genshin.hoyoverse.com/en/news/detail/5763)
we believe not. The scanner only takes screenshots and sends menu-navigation keystrokes; it provides
no exploits, progression, or account access, and interaction is limited to menus. That said, use of
any third-party tool is at your own discretion.

**Which languages are supported?**
English only, since the OCR model is trained exclusively on the English UI.

**Where can I ask for help?**
[Open a discussion](https://github.com/rogerioth/Inventory_Kamera/discussions) or an issue on GitHub.

## License

Distributed under the [MIT license](LICENSE). Third-party libraries are distributed under their own
licenses.

---

Not affiliated with or endorsed by HoYoverse. Genshin Impact™ and related properties belong to
© Cognosphere Pte. Ltd. and miHoYo.
