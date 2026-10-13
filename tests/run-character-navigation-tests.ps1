[CmdletBinding()]
param(
    [string]$CaptureDirectory,
    [Parameter(HelpMessage='Replay all saved Unknown world frames and Paimon entry frames through recognition and initial navigation.')]
    [string]$NavigationCaptureDirectory,
    [Parameter(HelpMessage='Replay saved glowing constellation base panes and negative detail overlays through real recognition and pane selection.')]
    [string]$PaneCaptureDirectory,
    [Parameter(HelpMessage='Replay the saved roster-count Attributes failure and its Attributes/Paimon entry frames without roster input.')]
    [string]$CountCaptureDirectory,
    [string]$PaimonScreenshotPath,
    [string]$DependencyDirectory,
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'InventoryKamera\InventoryKamera.csproj'
$applicationRoot = Split-Path -Parent $projectPath
$outputDirectory = Join-Path $repositoryRoot 'artifacts\character-navigation-tests'
$visualStudioLocator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'

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
    throw 'Pass -CompilerPath to Roslyn csc.exe or install Visual Studio with .NET desktop development.'
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
    throw 'Build InventoryKamera first, or pass -DependencyDirectory containing its restored runtime DLLs.'
}
$DependencyDirectory = (Resolve-Path -LiteralPath $DependencyDirectory).Path
$referenceDirectory = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2'
if (-not (Test-Path -LiteralPath (Join-Path $referenceDirectory 'mscorlib.dll'))) {
    throw 'The .NET Framework 4.7.2 targeting pack was not found.'
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$testExecutable = Join-Path $outputDirectory 'CharacterNavigationTests.exe'
$compilerArguments = [System.Collections.Generic.List[string]]::new()
foreach ($argument in @('/nologo', '/target:exe', '/platform:x64', '/langversion:7.3', '/nostdlib+', '/optimize+', '/main:InventoryKamera.Tests.CharacterNavigationTests')) {
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

# Fake navigation invokes only the controller's injected callbacks. Compile the
# exact source list with a console entry point; neither the app nor game opens.
foreach ($source in $projectXml.SelectNodes('//build:Compile', $namespaceManager)) {
    $sourcePath = Join-Path $applicationRoot ([string]$source.Include)
    if (-not (Test-Path -LiteralPath $sourcePath)) { throw "Project source does not exist: $sourcePath" }
    $compilerArguments.Add('"' + $sourcePath + '"')
}
$compilerArguments.Add('"' + (Join-Path $PSScriptRoot 'CharacterNavigationTests.cs') + '"')
$responseFile = Join-Path $outputDirectory 'compile.rsp'
[System.IO.File]::WriteAllLines($responseFile, $compilerArguments, [System.Text.UTF8Encoding]::new($false))
& $CompilerPath ('@' + $responseFile)
if ($LASTEXITCODE -ne 0) { throw "Character navigation harness compilation failed with exit code $LASTEXITCODE." }

$testArguments = @()
if ($CaptureDirectory) { $testArguments += @('--capture-directory', (Resolve-Path -LiteralPath $CaptureDirectory).Path) }
if ($NavigationCaptureDirectory) { $testArguments += @('--navigation-capture-directory', (Resolve-Path -LiteralPath $NavigationCaptureDirectory).Path) }
if ($PaneCaptureDirectory) { $testArguments += @('--pane-capture-directory', (Resolve-Path -LiteralPath $PaneCaptureDirectory).Path) }
if ($CountCaptureDirectory) { $testArguments += @('--count-capture-directory', (Resolve-Path -LiteralPath $CountCaptureDirectory).Path) }
if ($PaimonScreenshotPath) { $testArguments += @('--paimon-screenshot', (Resolve-Path -LiteralPath $PaimonScreenshotPath).Path) }
Push-Location -LiteralPath $outputDirectory
try {
    & $testExecutable @testArguments
    if ($LASTEXITCODE -ne 0) { throw "Character navigation regressions failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
