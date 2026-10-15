[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [string]$CaptureDirectory,
    [string]$DependencyDirectory,
    [string]$CompilerPath,
    [switch]$BuildProject
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'InventoryKamera\InventoryKamera.csproj'
$applicationRoot = Split-Path -Parent $projectPath
$outputDirectory = Join-Path $repositoryRoot 'artifacts\artifact-database-tests'
$visualStudioLocator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if ($CaptureDirectory -and -not $SourceDirectory) {
    throw 'Capture replay requires -SourceDirectory containing the saved source JSON files.'
}

if (-not $CompilerPath) {
    if (Test-Path -LiteralPath $visualStudioLocator) {
        $CompilerPath = & $visualStudioLocator -latest -products '*' -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
    }
    if (-not $CompilerPath) {
        $compilerCommand = Get-Command csc.exe -ErrorAction SilentlyContinue
        if ($compilerCommand) { $CompilerPath = $compilerCommand.Source }
    }
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath)) {
    throw 'A modern C# compiler was not found. Install Visual Studio with .NET desktop development, or pass -CompilerPath to Roslyn csc.exe.'
}

if ($BuildProject) {
    $buildTool = $null
    if (Test-Path -LiteralPath $visualStudioLocator) {
        $buildTool = & $visualStudioLocator -latest -products '*' -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
    if (-not $buildTool) {
        $buildCommand = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
        if ($buildCommand) { $buildTool = $buildCommand.Source }
    }
    if (-not $buildTool) { throw 'MSBuild was not found. Run from a Visual Studio Developer PowerShell.' }
    & $buildTool $projectPath '/restore' '/nologo' '/verbosity:minimal' '/p:Configuration=Debug' '/p:Platform=x64'
    if ($LASTEXITCODE -ne 0) { throw "Application build failed with exit code $LASTEXITCODE." }
}

if (-not $DependencyDirectory) {
    foreach ($relativeDirectory in @('bin\x64\Debug', 'bin\Debug', 'bin\Release', 'bin\Redesign')) {
        $candidateDirectory = Join-Path $applicationRoot $relativeDirectory
        if ((Test-Path -LiteralPath (Join-Path $candidateDirectory 'Newtonsoft.Json.dll')) -and
            (Test-Path -LiteralPath (Join-Path $candidateDirectory 'Tesseract.dll'))) {
            $DependencyDirectory = $candidateDirectory
            break
        }
    }
}
if (-not $DependencyDirectory -or -not (Test-Path -LiteralPath $DependencyDirectory)) {
    throw 'Build InventoryKamera first, use -BuildProject, or pass -DependencyDirectory containing its restored runtime DLLs.'
}
$DependencyDirectory = (Resolve-Path -LiteralPath $DependencyDirectory).Path
$referenceDirectory = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2'
if (-not (Test-Path -LiteralPath (Join-Path $referenceDirectory 'mscorlib.dll'))) {
    throw 'The .NET Framework 4.7.2 targeting pack was not found. Install it through Visual Studio Installer.'
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$testExecutable = Join-Path $outputDirectory 'ArtifactDatabaseTests.exe'
$compilerArguments = [System.Collections.Generic.List[string]]::new()
foreach ($argument in @('/nologo', '/target:exe', '/platform:x64', '/langversion:7.3', '/nostdlib+', '/optimize+', '/main:InventoryKamera.Tests.ArtifactDatabaseTests')) {
    $compilerArguments.Add($argument)
}
$compilerArguments.Add('/out:"' + $testExecutable + '"')
$compilerArguments.Add('/reference:"' + (Join-Path $referenceDirectory 'mscorlib.dll') + '"')
$compilerArguments.Add('/reference:"' + (Join-Path $referenceDirectory 'Facades\netstandard.dll') + '"')
[xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
$namespaceManager = [System.Xml.XmlNamespaceManager]::new($projectXml.NameTable)
$namespaceManager.AddNamespace('build', 'http://schemas.microsoft.com/developer/msbuild/2003')
foreach ($reference in $projectXml.SelectNodes('//build:Reference', $namespaceManager)) {
    $assemblyName = ([string]$reference.Include).Split(',')[0]
    $referencePath = Join-Path $referenceDirectory ($assemblyName + '.dll')
    if (-not (Test-Path -LiteralPath $referencePath)) {
        $referencePath = Join-Path $referenceDirectory ('WPF\' + $assemblyName + '.dll')
    }
    if (-not (Test-Path -LiteralPath $referencePath)) {
        throw "Framework reference $assemblyName was not found in the .NET Framework 4.7.2 targeting pack."
    }
    $compilerArguments.Add('/reference:"' + $referencePath + '"')
}
foreach ($runtimeLibrary in (Get-ChildItem -LiteralPath $DependencyDirectory -Filter '*.dll' -File)) {
    $compilerArguments.Add('/reference:"' + $runtimeLibrary.FullName + '"')
    Copy-Item -LiteralPath $runtimeLibrary.FullName -Destination $outputDirectory -Force
}
foreach ($architectureDirectory in @('x64', 'x86')) {
    $nativeDirectory = Join-Path $DependencyDirectory $architectureDirectory
    if (Test-Path -LiteralPath $nativeDirectory) {
        Copy-Item -LiteralPath $nativeDirectory -Destination $outputDirectory -Recurse -Force
    }
}
$modelDirectory = Join-Path $outputDirectory 'tessdata'
New-Item -ItemType Directory -Path $modelDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $applicationRoot 'tessdata\genshin_fast_09_04_21.traineddata') -Destination $modelDirectory -Force
$applicationConfig = Join-Path $DependencyDirectory 'InventoryKamera.exe.config'
if (Test-Path -LiteralPath $applicationConfig) {
    Copy-Item -LiteralPath $applicationConfig -Destination ($testExecutable + '.config') -Force
}

# Compile the exact project sources, with a console entry point, to exercise
# internal lookup generation without a test framework or any application input.
foreach ($source in $projectXml.SelectNodes('//build:Compile', $namespaceManager)) {
    $sourcePath = Join-Path $applicationRoot ([string]$source.Include)
    if (-not (Test-Path -LiteralPath $sourcePath)) { throw "Project source does not exist: $sourcePath" }
    $compilerArguments.Add('"' + $sourcePath + '"')
}
$compilerArguments.Add('"' + (Join-Path $PSScriptRoot 'ArtifactDatabaseTests.cs') + '"')
$responseFile = Join-Path $outputDirectory 'compile.rsp'
[System.IO.File]::WriteAllLines($responseFile, $compilerArguments, [System.Text.UTF8Encoding]::new($false))
& $CompilerPath ('@' + $responseFile)
if ($LASTEXITCODE -ne 0) { throw "Artifact database test compilation failed with exit code $LASTEXITCODE." }

$testArguments = @()
if ($SourceDirectory) { $testArguments += @('--source-directory', (Resolve-Path -LiteralPath $SourceDirectory).Path) }
if ($CaptureDirectory) { $testArguments += @('--capture-directory', (Resolve-Path -LiteralPath $CaptureDirectory).Path) }
Push-Location -LiteralPath $outputDirectory
try {
    & $testExecutable @testArguments
    if ($LASTEXITCODE -ne 0) { throw "Artifact database regression tests failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
