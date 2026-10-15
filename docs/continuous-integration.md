# Continuous integration

CircleCI also validates the release policy and builds a complete Windows package
on PRs, before Sonar analysis. This catches packaging failures before a merge.

The Windows build/check baseline has passed locally and in the initial CircleCI
hosted run. Its verified GitHub status is
`ci/circleci: Windows build and offline checks`.
The first token-backed PR analysis also passed its quality gate and published
`SonarCloud Code Analysis`. PR results cover the change under review; use the
`master` dashboard for the full project baseline.

## Windows builds and checks

[`.circleci/config.yml`](../.circleci/config.yml) defines one CircleCI job using
`windows.medium` and `windows-server-2022-gui:current`. It runs
[`tests/run-ci.ps1`](../tests/run-ci.ps1), which:

1. Restores and rebuilds the application project for Debug `AnyCPU`, then Debug
   `x64`. These builds run sequentially because their intermediate output is shared.
2. Runs the default achievement, character-navigation, artifact-database, and
   material-quantity harnesses against the x64 output: **27 grouped checks**.

Checkout explicitly uses `method: full`. CircleCI's default blobless checkout
can omit historical file contents needed by Sonar's Git blame reader, causing
`MissingObjectException: Missing blob` during a full project scan. See the
[checkout reference](https://circleci.com/docs/reference/configuration-reference/#checkout).

The script builds the classic .NET Framework 4.7.2 application `.csproj`, not
the installer project. It requires Windows, Visual Studio/MSBuild, Roslyn, the
4.7.2 targeting pack, and the Visual C++ 2015–2022 x64 runtime. NuGet supplies Tesseract's
native DLLs; the trained OCR model is committed in the repository.

Run the same baseline from the repository root:

```powershell
.\tests\run-ci.ps1
```

No app or live game opens. The default checks use synthetic data and injected
navigation; the blank-frame guard also exercises native
OCR. They do not download game data or need a private lookup cache.

Screenshot replays remain optional local checks because their captures and
catalogs are not committed. Traveler checks are also excluded from this baseline:
they require a saved game-data source directory. See [Development](development.md)
for the individual harness options.

## CircleCI project settings and budget

The selected CircleCI organization is GitHub OAuth `github/rogerioth`, on the
Free plan. Windows builds use its 30,000-credit monthly allowance; the larger
open-source allowance applies to Linux builds. Monitor the remaining allowance
in the service rather than assuming public Windows builds are unlimited.
See [CircleCI pricing](https://circleci.com/pricing/).

The OAuth project's settings enable **Only build pull requests**, **Build forked
pull requests**, and **Auto-cancel redundant workflows**. The default `master`
branch still builds. **Free and Open Source** is enabled, and **Pass secrets to
builds from forked pull requests** remains disabled. These service settings were
verified in the project UI; they are separate from the committed YAML.
See CircleCI's [open-source settings](https://circleci.com/docs/guides/integration/oss/)
and [redundant-workflow cancellation](https://circleci.com/docs/guides/orchestrate/skip-build/).

Contributor branches use `feature/<name>` or `fix/<name>`. Fork PR builds should
not receive secrets: keep **Pass secrets to builds from forked pull requests**
disabled. The build/check baseline requires no project token or secret.

## SonarQube Cloud

The public project `rogerioth_Inventory_Kamera` has been created in organization
`rogerioth` on its existing open-source plan. The selected new-code period is
**30 days**. Although Sonar documents .NET Framework automatic-analysis support,
this project's UI reports its configuration unsupported and automatic analysis
is off. The implementation therefore uses CI-based analysis.

The analysis step after the baseline checks runs
[`tests/run-sonar-analysis.ps1`](../tests/run-sonar-analysis.ps1): SonarScanner for
.NET **begin**, an application rebuild with Windows MSBuild, then **end** to
upload results. This is the C# analysis path described in Sonar's
[scanner documentation](https://docs.sonarsource.com/sonarqube-cloud/analyzing-source-code/scanners/sonarscanner-for-dotnet/using).
No CircleCI Sonar orb is used, and automatic analysis must remain off.
The tool manifest pins SonarScanner for .NET to version **11.3.0**. The script
checks the PR repository, branch, and head commit before uploading an analysis.

`SONAR_TOKEN` is stored in the CircleCI project's secret environment settings.
Sharing it with fork builds stays disabled. Fork PRs run the baseline checks and
skip Sonar upload; a skipped analysis does not establish a passing quality gate.
Missing credentials on `master` or a trusted PR fail the job so a broken secret
configuration cannot silently omit analysis. Rotate or revoke the dedicated
Sonar token through the account's security settings and replace its CircleCI
value; never put it in repository files or badge URLs.

The harness results are **not coverage measurements**. No coverage report is
currently generated or imported; coverage must not be inferred from the 27
checks or filled with invented values.

The project is assigned **Inventory Kamera - new code**, a project-specific copy
of Sonar way. Its new-code conditions require reliability, security, and
maintainability ratings of A, 100% security-hotspot review, and at most 3%
duplication. Coverage is not a gate condition until genuine coverage reports are
introduced. This avoids treating the current harness pass count as a coverage
measurement. The organization's default gate is unchanged.

The CircleCI status is required by the repository ruleset, including an
up-to-date branch. Trusted builds wait for the Sonar gate and fail if it fails.
The separate Sonar check is not required globally because fork PRs have no
analysis credential. Promote a reviewed fork change to a maintainer-controlled
branch and PR when a Sonar analysis is needed before merging it.
See [Repository review policy](repository-governance.md) for protection setup.

## Automatic releases

CircleCI remains the PR and default-branch build/test service. After a push to
`master`, [`.github/workflows/release.yml`](../.github/workflows/release.yml)
waits for the successful CircleCI build and its required GitHub status on that
**exact commit**. The trusted `master` job includes the Sonar quality-gate wait;
a passing PR build alone cannot authorize a release of its later merge commit.

A Windows job builds and checks the Release `x64` distribution with read-only
repository access. A separate publishing job receives only the packaged artifacts
and a temporary `GITHUB_TOKEN` with `contents: write` to create the tag and release.
It does not execute application or packaging code. No personal access token or
GitHub write credential is stored in CircleCI, and PRs do not publish releases.
See GitHub's [token permissions guidance](https://docs.github.com/en/actions/tutorials/authenticate-with-github_token).

Release jobs use standard GitHub-hosted runners, which are
[free for public repositories](https://docs.github.com/en/billing/concepts/product-billing/github-actions).
They do not select paid larger runners. Intermediate artifacts have short retention;
the downloadable distributions live on GitHub Releases. CircleCI Windows checks
still use the Free plan's credit allowance described above.

See [Releases and downloads](releases.md) for version calculation, included runtime
files, checksum verification, and retrying a failed publication.
