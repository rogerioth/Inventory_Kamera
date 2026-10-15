[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SourceDirectory,
    [string]$HeaderDirectory,
    [string]$DependencyDirectory,
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $DependencyDirectory) {
    $DependencyDirectory = Join-Path $repositoryRoot 'InventoryKamera\bin\x64\Debug'
}
$DependencyDirectory = (Resolve-Path -LiteralPath $DependencyDirectory).Path
if (-not $CompilerPath) {
    $visualStudioLocator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $visualStudioLocator) {
        $CompilerPath = & $visualStudioLocator -latest -products '*' -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
    }
    if (-not $CompilerPath) { $CompilerPath = (Get-Command csc.exe -ErrorAction Stop).Source }
}

# Exercise the built application's internal parser without game input or cache writes.
$testExecutable = Join-Path $DependencyDirectory 'TravelerTests.exe'
$newtonsoft = Join-Path $DependencyDirectory 'Newtonsoft.Json.dll'
& $CompilerPath /nologo /target:exe /platform:x64 /langversion:7.3 "/out:$testExecutable" "/reference:$newtonsoft" /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'TravelerTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Traveler harness compilation failed.' }
Copy-Item -LiteralPath (Join-Path $DependencyDirectory 'InventoryKamera.exe.config') -Destination ($testExecutable + '.config') -Force
$testArguments = @((Resolve-Path -LiteralPath $SourceDirectory).Path)
if ($HeaderDirectory) { $testArguments += (Resolve-Path -LiteralPath $HeaderDirectory).Path }
& $testExecutable @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Traveler checks failed.' }
