# Continuous integration

The Windows build/check baseline has passed locally and in the initial CircleCI
hosted run. Its verified GitHub status is
`ci/circleci: Windows build and offline checks`.
SonarQube Cloud analysis and its quality-gate result still need verification.

## Windows builds and checks

[`.circleci/config.yml`](../.circleci/config.yml) defines one CircleCI job using
`windows.medium` and `windows-server-2022-gui:current`. It runs
[`tests/run-ci.ps1`](../tests/run-ci.ps1), which:

1. Restores and rebuilds the application project for Debug `AnyCPU`, then Debug
   `x64`. These builds run sequentially because their intermediate output is shared.
2. Runs the default achievement, character-navigation, artifact-database, and
   material-quantity harnesses against the x64 output: **27 grouped checks**.

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

The analysis step being added after the baseline checks runs
[`tests/run-sonar-analysis.ps1`](../tests/run-sonar-analysis.ps1): SonarScanner for
.NET **begin**, an application rebuild with Windows MSBuild, then **end** to
upload results. This is the C# analysis path described in Sonar's
[scanner documentation](https://docs.sonarsource.com/sonarqube-cloud/analyzing-source-code/scanners/sonarscanner-for-dotnet/using).
No CircleCI Sonar orb is used, and automatic analysis must remain off.
The tool manifest pins SonarScanner for .NET to version **11.3.0**. The script
checks the PR repository, branch, and head commit before uploading an analysis.

`SONAR_TOKEN` belongs in the CircleCI project's secret environment settings.
Sharing it with fork builds stays disabled. Without that token, including on
fork PRs, the baseline builds and checks still run and the Sonar step skips its
upload. A skipped analysis does not establish a passing quality gate.

The harness results are **not coverage measurements**. No coverage report is
currently generated or imported; coverage must not be inferred from the 27
checks or filled with invented values.

The project is assigned **Inventory Kamera - new code**, a project-specific copy
of Sonar way. Its new-code conditions require reliability, security, and
maintainability ratings of A, 100% security-hotspot review, and at most 3%
duplication. Its inherited 80% coverage condition remains in place pending an
explicit decision about the initial policy without coverage reports. The
organization's default gate is unchanged.

The first uploaded analysis and PR gate result still need verification. Establish
a `master` baseline before relying on PR comparisons. After successful `master`
and PR analyses, confirm the exact Sonar check name and fork-PR behavior before
adding a separate required Sonar check. The verified CircleCI status is required
by the repository ruleset, including an up-to-date branch; a Sonar gate pass has
not yet been established.
See [Repository review policy](repository-governance.md) for protection setup.
