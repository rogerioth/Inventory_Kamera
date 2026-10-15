INVENTORY KAMERA - WINDOWS x64

1. Use Windows Explorer's "Extract All" on InventoryKamera-win-x64.zip.
   Extract to a normal writable folder such as Documents\Inventory Kamera.
   Do not run the program from inside the ZIP or move the EXE by itself.
2. Double-click "Start Inventory Kamera.cmd".
   The launcher checks prerequisites and installs the bundled, Microsoft-signed
   Visual C++ x64 runtime if needed. No separate download is required for it.
   If the installer requests a Windows restart, restart and launch again.
3. Allow the application's Windows administrator prompt. Inventory Kamera
   requires this to interact with an elevated game window.

On subsequent runs, InventoryKamera.exe can also be opened directly once the
prerequisites are present. Keep all DLLs, the x64 folder and tessdata beside it.
The package includes both trained OCR models and native OCR libraries.

Requirements: 64-bit Windows 10/11, .NET Framework 4.7.2 or newer (4.8 is fine),
and the English game interface. Windows 11 includes a compatible .NET Framework.
If .NET is missing, the launcher provides Microsoft's official download link.
The scanner downloads its current game lookup tables on first use; an internet
connection is needed for that update.

This application is not code-signed. Windows may show a SmartScreen warning.
Only run a package obtained from the project's official GitHub Releases page:
https://github.com/rogerioth/Inventory_Kamera/releases/latest
SHA256SUMS.txt in the release assets lets you verify the ZIP's download checksum.

Settings, logs and scan exports are written under the extracted folder. When
updating, extract to a new folder rather than overwriting a running copy.
See LICENSE, THIRD-PARTY-NOTICES.txt and licenses/ for component licenses.
release-metadata.json identifies this package's version and source commit.

Usage instructions and issue reports:
https://github.com/rogerioth/Inventory_Kamera
https://github.com/rogerioth/Inventory_Kamera/issues
