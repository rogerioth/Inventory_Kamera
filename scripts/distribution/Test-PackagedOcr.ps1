param([Parameter(Mandatory = $true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
try {
    if (-not [Environment]::Is64BitProcess) { throw 'Packaged OCR validation requires 64-bit PowerShell.' }
    Set-Location -LiteralPath $PackageDirectory
    Add-Type -Path (Join-Path $PackageDirectory 'Tesseract.dll')
    foreach ($model in @('genshin_best_eng', 'genshin_fast_09_04_21')) {
        $engine = [Tesseract.TesseractEngine]::new((Join-Path $PackageDirectory 'tessdata'), $model, [Tesseract.EngineMode]::Default)
        try { Write-Host "Packaged OCR model loaded: $model" }
        finally { $engine.Dispose() }
    }
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
