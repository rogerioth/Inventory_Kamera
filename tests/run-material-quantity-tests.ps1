[CmdletBinding()]
param(
    [string]$CaptureDirectory,
    [string[]]$ScreenshotPath,
    [Nullable[int]]$ExpectedQuantity,
    [string]$DependencyDirectory,
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $DependencyDirectory) {
    $DependencyDirectory = Join-Path $repositoryRoot 'InventoryKamera\bin\x64\Debug'
}
$DependencyDirectory = (Resolve-Path -LiteralPath $DependencyDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $DependencyDirectory 'InventoryKamera.exe'))) {
    throw 'Build InventoryKamera first or pass -DependencyDirectory pointing to a built app.'
}
if (-not $CompilerPath) {
    $visualStudioLocator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $visualStudioLocator) {
        $CompilerPath = & $visualStudioLocator -latest -products '*' -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
    }
    if (-not $CompilerPath) { $CompilerPath = (Get-Command csc.exe -ErrorAction Stop).Source }
}

# This reflection harness uses the built app and its OCR model/native DLLs.
# Keep its generated executable beside those dependencies; never launch the app.
$testExecutable = Join-Path $DependencyDirectory 'MaterialQuantityTests.exe'
& $CompilerPath /nologo /target:exe /platform:x64 /langversion:7.3 "/out:$testExecutable" /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'MaterialQuantityTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Material quantity harness compilation failed.' }
Copy-Item -LiteralPath (Join-Path $DependencyDirectory 'InventoryKamera.exe.config') -Destination ($testExecutable + '.config') -Force
$testArguments = @()
if ($CaptureDirectory) { $testArguments += @('--capture-directory', (Resolve-Path -LiteralPath $CaptureDirectory).Path) }
if ($ScreenshotPath) {
    if ($null -eq $ExpectedQuantity -or $ExpectedQuantity -le 0) {
        throw 'Pass a positive -ExpectedQuantity when replaying -ScreenshotPath.'
    }
    foreach ($screenshot in $ScreenshotPath) {
        $testArguments += @('--screenshot', (Resolve-Path -LiteralPath $screenshot).Path, [string]$ExpectedQuantity)
    }
}
& $testExecutable @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Material quantity checks failed.' }
