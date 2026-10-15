[CmdletBinding()]
param(
    [string]$Branch = $env:CIRCLE_BRANCH,
    [string]$PullRequestUrl = $env:CIRCLE_PULL_REQUEST,
    [string]$MSBuildPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'InventoryKamera\InventoryKamera.csproj'
$toolManifestPath = Join-Path $repositoryRoot '.config\dotnet-tools.json'
$repositoryName = 'rogerioth/Inventory_Kamera'
$defaultBranch = 'master'

# Fork PRs do not receive project secrets. A skipped analysis is not a passed
# Sonar quality gate; the build and offline checks have their own CI step.
if ([string]::IsNullOrWhiteSpace($env:SONAR_TOKEN)) {
    Write-Host 'SKIP Sonar analysis: SONAR_TOKEN is unavailable. No Sonar quality gate was evaluated.'
    return
}

Push-Location $repositoryRoot
try {
    if ([string]::IsNullOrWhiteSpace($Branch)) {
        $Branch = & git branch --show-current
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Branch)) {
            throw 'Set -Branch when running outside a named CircleCI or local Git branch.'
        }
    }

    $pullRequest = $null
    $githubHeaders = @{
        'User-Agent' = 'InventoryKamera-Sonar-analysis'
        'Accept' = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    if ($Branch -ne $defaultBranch) {
        if (-not [string]::IsNullOrWhiteSpace($PullRequestUrl)) {
            $pullRequestUri = [Uri]$PullRequestUrl
            if ($pullRequestUri.Scheme -ne 'https' -or $pullRequestUri.Host -ne 'github.com' -or
                $pullRequestUri.AbsolutePath -notmatch '^/rogerioth/Inventory_Kamera/pull/([0-9]+)$') {
                throw 'The pull request URL must identify a PR in rogerioth/Inventory_Kamera on GitHub.'
            }
            $pullRequestNumber = $Matches[1]
            $pullRequest = Invoke-RestMethod -Uri "https://api.github.com/repos/$repositoryName/pulls/$pullRequestNumber" -Headers $githubHeaders -TimeoutSec 30
        }
        else {
            # OAuth pipelines normally expose CIRCLE_PULL_REQUEST. Query the
            # public API if it is absent instead of analyzing a feature branch
            # as the main branch or guessing its pull request number.
            $headQuery = [Uri]::EscapeDataString('rogerioth:' + $Branch)
            $openPullRequests = @(Invoke-RestMethod -Uri "https://api.github.com/repos/$repositoryName/pulls?state=open&base=master&head=$headQuery" -Headers $githubHeaders -TimeoutSec 30)
            if ($openPullRequests.Count -eq 0) {
                Write-Host 'SKIP Sonar analysis: this branch has no open pull request targeting master. No Sonar quality gate was evaluated.'
                return
            }
            if ($openPullRequests.Count -ne 1) {
                throw 'More than one open master-targeting PR matched this branch; analysis metadata is ambiguous.'
            }
            $pullRequest = $openPullRequests[0]
        }

        if ($pullRequest.base.repo.full_name -ne $repositoryName) {
            throw 'The pull request target repository does not match the Sonar project.'
        }
        if ($pullRequest.head.repo.full_name -ne $repositoryName) {
            Write-Host 'SKIP Sonar analysis: fork pull requests run build/tests without Sonar credentials. No Sonar quality gate was evaluated.'
            return
        }
        if ($pullRequest.state -ne 'open' -or $pullRequest.base.ref -ne $defaultBranch) {
            Write-Host 'SKIP Sonar analysis: only open pull requests targeting master are analyzed. No Sonar quality gate was evaluated.'
            return
        }
        if ($pullRequest.head.ref -ne $Branch) {
            throw 'The checked-out branch does not match the pull request source branch.'
        }
    }

    $checkedOutSha = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the checked-out Git revision.' }
    $checkedOutSha = $checkedOutSha.Trim()
    if ($env:CIRCLE_SHA1 -and $checkedOutSha -ne $env:CIRCLE_SHA1) {
        throw 'The checked-out Git revision does not match this CircleCI pipeline.'
    }
    if ($pullRequest -and $checkedOutSha -ne $pullRequest.head.sha) {
        throw 'The pull request head changed since this checkout. Analyze the latest pipeline instead.'
    }

    $dotnetCommand = Get-Command dotnet.exe -ErrorAction Stop
    $visualStudioLocator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not $MSBuildPath -and (Test-Path -LiteralPath $visualStudioLocator)) {
        $MSBuildPath = & $visualStudioLocator -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
    if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath)) {
        throw 'MSBuild was not found. Install Visual Studio with .NET desktop development, or pass -MSBuildPath.'
    }

    # The scanner needs history and the PR target branch for new-code analysis.
    $isShallow = & git rev-parse --is-shallow-repository
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect Git checkout history.' }
    if ($isShallow -eq 'true') {
        & git fetch --no-tags --unshallow "https://github.com/$repositoryName.git"
        if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the Git history required by Sonar.' }
    }
    if ($pullRequest) {
        & git fetch --no-tags "https://github.com/$repositoryName.git" '+refs/heads/master:refs/remotes/origin/master'
        if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the pull request target branch for Sonar.' }
    }

    Write-Host 'Restoring pinned SonarScanner for .NET 11.3.0'
    & $dotnetCommand.Source tool restore --tool-manifest $toolManifestPath
    if ($LASTEXITCODE -ne 0) { throw 'SonarScanner tool restore failed.' }

    $beginArguments = @(
        'tool', 'run', 'dotnet-sonarscanner', '--', 'begin',
        '/k:rogerioth_Inventory_Kamera',
        '/o:rogerioth',
        "/d:sonar.token=$env:SONAR_TOKEN",
        "/d:sonar.projectBaseDir=$repositoryRoot",
        "/d:sonar.scm.revision=$checkedOutSha",
        '/d:sonar.exclusions=**/bin/**,**/obj/**,artifacts/**,.sonarqube/**,.scannerwork/**',
        '/d:sonar.qualitygate.wait=true',
        '/d:sonar.qualitygate.timeout=300'
    )
    if ($pullRequest) {
        Write-Host ("Analyzing pull request #{0}: {1} into {2}" -f $pullRequest.number, $Branch, $defaultBranch)
        $beginArguments += "/d:sonar.pullrequest.key=$($pullRequest.number)"
        $beginArguments += "/d:sonar.pullrequest.branch=$Branch"
        $beginArguments += "/d:sonar.pullrequest.base=$defaultBranch"
    }
    else {
        Write-Host 'Analyzing master'
        $beginArguments += '/d:sonar.branch.name=master'
    }

    # Do not echo scanner arguments: the .NET scanner requires the token in
    # both begin and end. Do not save it in a response/configuration file.
    & $dotnetCommand.Source @beginArguments
    if ($LASTEXITCODE -ne 0) { throw 'SonarScanner begin failed.' }

    & $MSBuildPath $projectPath '/restore' '/t:Rebuild' '/nodeReuse:false' '/nologo' '/verbosity:minimal' '/p:Configuration=Debug' '/p:Platform=AnyCPU'
    if ($LASTEXITCODE -ne 0) { throw 'The Sonar-instrumented application build failed; analysis was not submitted.' }

    $endArguments = @('tool', 'run', 'dotnet-sonarscanner', '--', 'end', "/d:sonar.token=$env:SONAR_TOKEN")
    & $dotnetCommand.Source @endArguments
    if ($LASTEXITCODE -ne 0) { throw 'Sonar analysis or quality gate failed. See the scanner output for the reported result.' }
    Write-Host 'Sonar analysis completed and its quality gate passed.'
}
finally {
    Pop-Location
}
