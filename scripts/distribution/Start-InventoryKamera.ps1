$ErrorActionPreference = 'Stop'
$packageDirectory = Split-Path -Parent $PSScriptRoot

function Get-InstalledRuntimeVersion {
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $registry.OpenSubKey('SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64')
        try {
            if ($key -and $key.GetValue('Installed') -eq 1) {
                return [version]([string]$key.GetValue('Version')).TrimStart('v', 'V')
            }
        }
        finally { if ($key) { $key.Dispose() } }
    }
    finally { $registry.Dispose() }
    return [version]'0.0'
}

try {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'Inventory Kamera requires 64-bit Windows 10 or Windows 11.' }
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $frameworkKey = $registry.OpenSubKey('SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full')
        try { $frameworkRelease = if ($frameworkKey) { $frameworkKey.GetValue('Release', 0) } else { 0 } }
        finally { if ($frameworkKey) { $frameworkKey.Dispose() } }
    }
    finally { $registry.Dispose() }
    if ($frameworkRelease -lt 461808) {
        throw '.NET Framework 4.7.2 or newer is required. Install Microsoft .NET Framework 4.8 from https://dotnet.microsoft.com/download/dotnet-framework/net48 then run this launcher again.'
    }

    $requiredRuntime = [version](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime-version.txt') -Raw).Trim()
    if ((Get-InstalledRuntimeVersion) -lt $requiredRuntime) {
        $installer = Join-Path $PSScriptRoot 'vc_redist.x64.exe'
        $signature = Get-AuthenticodeSignature -LiteralPath $installer
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') {
            throw 'The bundled Microsoft prerequisite installer could not be verified. Download a fresh release ZIP from the project GitHub Releases page.'
        }
        Write-Host 'Installing the bundled Microsoft Visual C++ runtime. Please allow the Windows administrator prompt.'
        $process = Start-Process -FilePath $installer -ArgumentList '/install', '/passive', '/norestart' -Verb RunAs -Wait -PassThru
        if ($process.ExitCode -eq 3010) { throw 'Microsoft Visual C++ installation completed and requires a restart. Restart Windows, then run this launcher again.' }
        if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 1638) {
            throw "Microsoft Visual C++ installation failed (exit code $($process.ExitCode)). See the installer log in your Windows TEMP folder."
        }
        if ((Get-InstalledRuntimeVersion) -lt $requiredRuntime) { throw 'The required Microsoft Visual C++ runtime is still unavailable after installation.' }
    }
    Write-Host 'Starting Inventory Kamera. Allow its administrator prompt so it can read and control the game.'
    Start-Process -FilePath (Join-Path $packageDirectory 'InventoryKamera.exe') -WorkingDirectory $packageDirectory
    exit 0
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
