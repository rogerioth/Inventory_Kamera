[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [string]$OutputDirectory,
    [string]$MSBuildPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectDirectory = Join-Path $repositoryRoot 'InventoryKamera'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot 'artifacts\release' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$workDirectory = Join-Path $OutputDirectory 'build'
$binaryDirectory = Join-Path $workDirectory 'output'
$packageDirectory = Join-Path $workDirectory 'package'
$archivePath = Join-Path $OutputDirectory 'InventoryKamera-win-x64.zip'
$fileVersion = "$Version.0"
foreach ($component in $Version.Split('.')) {
    if ([long]$component -gt 65534) { throw 'Each version component must be between 0 and 65534 for .NET assembly metadata.' }
}

# Only this script-owned build directory is disposable. Never delete the caller's
# output directory, which might also hold already published release assets.
if (Test-Path -LiteralPath $workDirectory) {
    $resolvedWorkDirectory = (Resolve-Path -LiteralPath $workDirectory).Path
    if (-not $resolvedWorkDirectory.StartsWith($OutputDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a build directory outside $OutputDirectory."
    }
    Remove-Item -LiteralPath $resolvedWorkDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $binaryDirectory, $packageDirectory -Force | Out-Null

$visualStudioLocator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not $MSBuildPath -and (Test-Path -LiteralPath $visualStudioLocator)) {
    $MSBuildPath = & $visualStudioLocator -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath)) {
    throw 'MSBuild was not found. Install Visual Studio .NET desktop development, or pass -MSBuildPath.'
}

# Generate release-only version metadata without changing the checkout or making
# a second version-bump commit. Normal Visual Studio builds retain their version.
$assemblyInfo = Get-Content -LiteralPath (Join-Path $projectDirectory 'Properties\AssemblyInfo.cs') -Raw
$assemblyInfo = [regex]::Replace($assemblyInfo, '(?m)^\[assembly:\s*AssemblyVersion\("[^"]*"\)\]', "[assembly: AssemblyVersion(`"$fileVersion`")]")
$assemblyInfo += "`r`n[assembly: AssemblyFileVersion(`"$fileVersion`")]`r`n[assembly: AssemblyInformationalVersion(`"$Version`")]`r`n"
$generatedAssemblyInfo = Join-Path $workDirectory 'ReleaseAssemblyInfo.cs'
[IO.File]::WriteAllText($generatedAssemblyInfo, $assemblyInfo, [Text.UTF8Encoding]::new($false))
$escapedAssemblyInfo = [Security.SecurityElement]::Escape($generatedAssemblyInfo)
$hookPath = Join-Path $workDirectory 'ReleaseVersion.targets'
@"
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Target Name="UseReleaseAssemblyInfo" BeforeTargets="CoreCompile">
    <ItemGroup>
      <Compile Remove="Properties\AssemblyInfo.cs" />
      <Compile Include="$escapedAssemblyInfo" />
    </ItemGroup>
  </Target>
</Project>
"@ | Set-Content -LiteralPath $hookPath -Encoding UTF8

Write-Host "Building Windows x64 release $Version"
& $MSBuildPath (Join-Path $projectDirectory 'InventoryKamera.csproj') '/restore' '/t:Rebuild' '/nologo' '/verbosity:minimal' "/p:Configuration=Release" '/p:Platform=x64' "/p:OutputPath=$binaryDirectory\" "/p:CustomBeforeMicrosoftCSharpTargets=$hookPath" '/p:DebugSymbols=false' '/p:DebugType=None' '/nodeReuse:false'
if ($LASTEXITCODE -ne 0) { throw "Release x64 build failed with exit code $LASTEXITCODE." }

# Copy only distributable build products, never local settings, game data, logs,
# exports, test executables, symbols, or developer tools left in an output folder.
Get-ChildItem -LiteralPath $binaryDirectory -File | Where-Object {
    $_.Extension -eq '.dll' -or $_.Name -like '*.dll.config' -or $_.Name -in @('InventoryKamera.exe', 'InventoryKamera.exe.config')
} | Copy-Item -Destination $packageDirectory
foreach ($directory in @('x64', 'tessdata')) {
    Copy-Item -LiteralPath (Join-Path $binaryDirectory $directory) -Destination $packageDirectory -Recurse
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'distribution\README-FIRST.txt') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'distribution\Start Inventory Kamera.cmd') -Destination $packageDirectory
$supportDirectory = Join-Path $packageDirectory 'support'
New-Item -ItemType Directory -Path $supportDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'distribution\Start-InventoryKamera.ps1') -Destination $supportDirectory

# Bundle Microsoft's original signed installer. Central installation lets Windows
# service these libraries independently; users do not have to find a download.
$runtimeInstaller = Join-Path $supportDirectory 'vc_redist.x64.exe'
$runtimeUrl = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $runtimeUrl -OutFile $runtimeInstaller -UseBasicParsing
$signature = Get-AuthenticodeSignature -LiteralPath $runtimeInstaller
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') {
    throw "The Visual C++ runtime download does not have a valid Microsoft signature (status $($signature.Status))."
}
$runtimeVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($runtimeInstaller)
$runtimeVersion = '{0}.{1}.{2}.{3}' -f $runtimeVersionInfo.FileMajorPart, $runtimeVersionInfo.FileMinorPart, $runtimeVersionInfo.FileBuildPart, $runtimeVersionInfo.FilePrivatePart
$runtimeVersion | Set-Content -LiteralPath (Join-Path $supportDirectory 'runtime-version.txt') -Encoding ASCII

# Preserve dependency metadata and embedded license texts supplied by NuGet.
# The manifest also records the upstream license/source URLs for older packages.
$licenseDirectory = Join-Path $packageDirectory 'licenses'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'distribution\licenses') -File | Copy-Item -Destination $licenseDirectory
$assets = Get-Content -LiteralPath (Join-Path $projectDirectory 'obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
$notices = [Collections.Generic.List[string]]::new()
$notices.Add('THIRD-PARTY COMPONENTS')
$notices.Add('Inventory Kamera includes the following unmodified NuGet components. Each retains its own license. See licenses/ for package metadata and bundled license/notice files, and upstream URLs for source code and complete notices.')
foreach ($library in $assets.libraries.PSObject.Properties | Sort-Object Name) {
    if ($library.Value.type -ne 'package') { continue }
    $packagePath = $null
    foreach ($packageRoot in $packageRoots) {
        $candidate = Join-Path $packageRoot $library.Value.path
        if (Test-Path -LiteralPath $candidate) { $packagePath = $candidate; break }
    }
    if (-not $packagePath) { throw "Cannot find restored package $($library.Name) for license metadata." }
    $nuspec = Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' -File | Select-Object -First 1
    if (-not $nuspec) { throw "Missing NuGet metadata for $($library.Name)." }
    [xml]$packageManifest = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $packageManifest.package.metadata
    $dependencyLicenseDirectory = Join-Path $licenseDirectory $library.Name.Replace('/', '-')
    New-Item -ItemType Directory -Path $dependencyLicenseDirectory -Force | Out-Null
    Copy-Item -LiteralPath $nuspec.FullName -Destination $dependencyLicenseDirectory
    foreach ($relativePath in $library.Value.files) {
        if ([IO.Path]::GetFileName($relativePath) -match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD.PARTY)([.-].*)?$') {
            $noticePath = Join-Path $packagePath $relativePath
            if (Test-Path -LiteralPath $noticePath -PathType Leaf) {
                Copy-Item -LiteralPath $noticePath -Destination $dependencyLicenseDirectory
            }
        }
    }
    $notices.Add("`r`n$($library.Name)")
    foreach ($property in @('authors', 'copyright', 'license', 'licenseUrl', 'projectUrl')) {
        $value = $metadata.$property
        if ($null -ne $value -and "$value") {
            $textValue = if ($value -is [Xml.XmlNode]) { $value.InnerText } else { [string]$value }
            $notices.Add("${property}: $textValue")
        }
    }
}
$notices.Add("`r`nMicrosoft Visual C++ Redistributable: original signed installer bundled in support/. Microsoft Software License Terms apply. https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files")
$notices.Add("`r`nTesseract native engine (Apache-2.0): https://github.com/tesseract-ocr/tesseract ; Leptonica (BSD-2-Clause): https://github.com/DanBloomberg/leptonica ; native binaries are supplied by the Charles Weld Tesseract NuGet package.")
$notices.Add("`r`nAccord.NET 3.8.0 (LGPL-2.1-or-later) is dynamically linked through the unmodified Accord*.dll files. Its full license and copyright notice are included in licenses/Accord.NET-3.8.0-LGPL.txt. Corresponding upstream source: https://github.com/accord-net/framework/tree/v3.8.0 ; source archive: https://github.com/accord-net/framework/archive/refs/tags/v3.8.0.zip . Compatible modified shared libraries may replace these DLLs for personal use and debugging.")
$notices | Set-Content -LiteralPath (Join-Path $packageDirectory 'THIRD-PARTY-NOTICES.txt') -Encoding UTF8

$requiredFiles = @(
    'InventoryKamera.exe', 'InventoryKamera.exe.config', 'Tesseract.dll',
    'x64\tesseract50.dll', 'x64\leptonica-1.82.0.dll',
    'tessdata\genshin_best_eng.traineddata', 'tessdata\genshin_fast_09_04_21.traineddata',
    'Start Inventory Kamera.cmd', 'support\Start-InventoryKamera.ps1',
    'support\vc_redist.x64.exe', 'support\runtime-version.txt', 'LICENSE', 'README-FIRST.txt'
)
foreach ($relativePath in $requiredFiles) {
    $file = Get-Item -LiteralPath (Join-Path $packageDirectory $relativePath)
    if ($file.Length -eq 0) { throw "Empty release file: $relativePath" }
}
$assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $packageDirectory 'InventoryKamera.exe'))
$binaryReader = [IO.BinaryReader]::new([IO.File]::OpenRead((Join-Path $packageDirectory 'InventoryKamera.exe')))
try {
    $binaryReader.BaseStream.Position = 0x3c
    $binaryReader.BaseStream.Position = $binaryReader.ReadInt32()
    if ($binaryReader.ReadUInt32() -ne 0x4550) { throw 'Release executable has an invalid PE header.' }
    $machine = $binaryReader.ReadUInt16()
}
finally { $binaryReader.Dispose() }
if ($assembly.Version.ToString() -ne $fileVersion -or $machine -ne 0x8664) {
    throw "Release assembly identity is incorrect: version $($assembly.Version), PE machine $machine."
}
$commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot determine release source commit.' }
$metadata = [ordered]@{
    version = $Version; tag = "v$Version"; commit = $commit; fileVersion = $fileVersion
    architecture = 'x64'; framework = '.NET Framework 4.7.2'; runtimeVersion = $runtimeVersion
    archiveFile = [IO.Path]::GetFileName($archivePath)
}
[IO.File]::WriteAllText((Join-Path $packageDirectory 'release-metadata.json'), ($metadata | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

# Exercise the actual packaged OCR binaries/models in an isolated process. This
# does not start the app, capture the screen, send input, or access the game.
& (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'distribution\Test-PackagedOcr.ps1') -PackageDirectory $packageDirectory
if ($LASTEXITCODE -ne 0) { throw "Packaged OCR smoke check failed with exit code $LASTEXITCODE." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entries = @($archive.Entries.FullName | ForEach-Object { $_.Replace('/', '\') })
    foreach ($relativePath in $requiredFiles + @('release-metadata.json', 'THIRD-PARTY-NOTICES.txt')) {
        if ($relativePath -notin $entries) { throw "Release ZIP is missing $relativePath." }
    }
    if ($entries | Where-Object { $_ -match '(^|\\)(logging|GenshinData|obj|bin)(\\|$)|\.(pdb|user|binlog)$|(^|\\)x86\\' }) {
        throw 'Release ZIP contains a prohibited development or user-data file.'
    }
}
finally { $archive.Dispose() }
$metadata['sha256'] = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$($metadata.sha256)  $($metadata.archiveFile)" | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ASCII
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'release-metadata.json'), ($metadata | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Host "Release package verified: $archivePath"
Write-Host "SHA256: $($metadata.sha256)"
