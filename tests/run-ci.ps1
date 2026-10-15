[CmdletBinding()]
param(
    [string]$MSBuildPath,
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'InventoryKamera\InventoryKamera.csproj'
$dependencyDirectory = Join-Path $repositoryRoot 'InventoryKamera\bin\x64\Debug'
$visualStudioLocator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'

if (Test-Path -LiteralPath $visualStudioLocator) {
    if (-not $MSBuildPath) {
        $MSBuildPath = & $visualStudioLocator -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
    if (-not $CompilerPath) {
        $CompilerPath = & $visualStudioLocator -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
    }
}
if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath)) {
    throw 'MSBuild was not found. Install Visual Studio with .NET desktop development, or pass -MSBuildPath.'
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath)) {
    throw 'Roslyn csc.exe was not found. Install Visual Studio with .NET desktop development, or pass -CompilerPath.'
}
$referenceDirectory = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2'
if (-not (Test-Path -LiteralPath (Join-Path $referenceDirectory 'mscorlib.dll'))) {
    throw 'Install the .NET Framework 4.7.2 targeting pack through Visual Studio Installer.'
}

Push-Location $repositoryRoot
try {
    # The configurations share intermediate output, so rebuild sequentially.
    # Build the application project; the solution also includes an installer.
    foreach ($platform in @('AnyCPU', 'x64')) {
        Write-Host "Building Debug $platform"
        & $MSBuildPath $projectPath '/restore' '/t:Rebuild' '/nologo' '/verbosity:minimal' '/p:Configuration=Debug' "/p:Platform=$platform"
        if ($LASTEXITCODE -ne 0) {
            throw "Debug $platform build failed with exit code $LASTEXITCODE."
        }
    }

    # Default checks use synthetic data and fake navigation. Do not pass capture
    # or source directories: those optional replays need local, ignored files.
    foreach ($runner in @(
        'run-achievement-tests.ps1',
        'run-character-navigation-tests.ps1',
        'run-artifact-database-tests.ps1',
        'run-material-quantity-tests.ps1'
    )) {
        Write-Host "Running $runner"
        & (Join-Path $PSScriptRoot $runner) -DependencyDirectory $dependencyDirectory -CompilerPath $CompilerPath
    }
    Write-Host 'CI builds and offline checks completed.'
}
finally {
    Pop-Location
}
