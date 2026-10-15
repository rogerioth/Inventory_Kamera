# Releases and downloads

The repository's default branch is **`master`**. Each merge into it automatically
publishes a Windows release after the merge commit passes CircleCI, including the
Sonar quality gate. Tags record the exact source commit used to build each release.

## Download and run

Download **[InventoryKamera-win-x64.zip](https://github.com/rogerioth/Inventory_Kamera/releases/latest/download/InventoryKamera-win-x64.zip)**
from the [latest release](https://github.com/rogerioth/Inventory_Kamera/releases/latest).
GitHub also lists automatically generated **Source code** archives; those contain
source files and are not the application download.

1. Extract the entire application ZIP into a new, writable folder on Windows 10
   or 11 x64. Keep all the included files and folders together.
2. Double-click **`Start Inventory Kamera.cmd`** in the extracted folder.
3. Complete the bundled Microsoft Visual C++ runtime installer if the launcher
   reports that installation or an update is needed. The app opens automatically
   after a successful install. If a restart is required, restart Windows and run
   the launcher again.
4. Approve the scanner's administrator prompt so it can navigate the game menus.

The launcher checks for .NET Framework 4.7.2 or newer. If it is missing, follow
its Microsoft download instructions before launching again. Recent Windows
installations generally include a compatible framework. Developers need Visual
Studio to build the app; users of the ZIP do not.

The package contains the Release `x64` executable, managed and native dependencies,
trained OCR models, startup instructions, licenses, and Microsoft's signed Visual
C++ redistributable. Game lookup tables are downloaded on first use and refreshed
through the app's data updater, so initial setup and data updates need internet
access. Read [Keeping data current](../README.md#keeping-data-current) for details.

`InventoryKamera.exe` requests administrator permission because game input may
otherwise be blocked. The scanner is not code-signed; Windows SmartScreen can
display a warning. Bundling a signed Microsoft runtime does not sign the scanner.
Download from this repository's releases and check the published checksum when
verifying a download.

## Release assets and checksums

Each release has these assets:

| Asset | Purpose |
| --- | --- |
| `InventoryKamera-win-x64.zip` | Complete Windows distribution; recommended download |
| `SHA256SUMS.txt` | SHA-256 checksums to detect changed or incomplete downloads |
| `release-metadata.json` | Release version and source commit for tracing a package |

The ZIP name stays constant across releases, so the README's latest-download link
does not need changing after each publication. The release tag identifies the
version. To check a downloaded ZIP, run this in its download folder and compare
the hash with the ZIP entry in `SHA256SUMS.txt` from the same release:

```powershell
Get-FileHash .\InventoryKamera-win-x64.zip -Algorithm SHA256
```

A checksum establishes that the bytes match the published release; it is not
a code-signing certificate.

## Merge, validate, package, publish

The workflow is defined in
[`.github/workflows/release.yml`](../.github/workflows/release.yml).

1. A push to `master` starts the release workflow. PRs and tag pushes do not
   publish releases.
2. The workflow waits for CircleCI's successful default-branch build on the exact
   source SHA and verifies the required `ci/circleci: Windows build and offline checks`
   GitHub status. This includes the normal offline checks and trusted-branch Sonar
   gate. It does not reuse the PR head's result for a different merge commit.
3. A read-only Windows build job runs
   [`scripts/build-release.ps1`](../scripts/build-release.ps1) to rebuild the
   application as Release `x64` and validate the distribution contents. The job
   downloads the official Microsoft redistributable and verifies its signature
   before including it in the ZIP.
4. A separate job publishes the package using GitHub's temporary `GITHUB_TOKEN`,
   with `contents: write` only in that job. Application builds have no release
   write credential. A GitHub PAT is not required in either service.
5. Publishing creates a version tag at the validated SHA, creates a draft,
   uploads all assets, and then publishes it with generated release notes. Users
   receive a complete release rather than an initially empty download page.

CircleCI also tests the release policy and builds a package on PRs. Publishing
rechecks CI immediately before creating a release. The temporary Actions artifact
used to transfer the package is deleted after successful publication; on failure
it expires after one day. The persistent downloads live in GitHub Releases.

Publishing jobs are queued rather than canceled when another merge arrives.
Version calculation depends on source history, not job completion order. An older
commit finishing later does not replace a newer published version as **Latest**.
The release workflow uses standard public-repository GitHub-hosted runners;
CircleCI retains responsibility for the CI checks and their Free-plan credit use.

## Version numbers

[`release-version.json`](../release-version.json) records a baseline version and
an existing baseline commit. The release patch number adds the number of
first-parent commits between that baseline and the source commit:

```text
release version = baseline major.minor.(baseline patch + first-parent distance)
tag             = v<release version>
```

The initial baseline is **1.3.17** at
`98a14bc66e13c6216b9e65c61bacbccdfd4f64f8`; the next commit on the first-parent
`master` history becomes **1.3.18**, tagged **v1.3.18**. A squash merge advances
the patch once, irrespective of the contributor branch's number of commits.
The package receives the calculated version during its build; no automatic
version-bump commit is pushed back into the protected branch.

This is automatic patch versioning, not an interpretation of Conventional Commit
messages. For a planned major or minor series change, review an update to the
baseline version/commit pair in a PR. Account for the first-parent distance in
the formula before merging, and never assign a version already published for a
different commit. The version and publication policy helpers are
[`scripts/release-version.cjs`](../scripts/release-version.cjs) and
[`scripts/release-policy.cjs`](../scripts/release-policy.cjs).

## Failure recovery

A failed, missing, or timed-out CircleCI build blocks publication. Inspect the
exact commit's [CircleCI pipeline](https://app.circleci.com/pipelines/github/rogerioth/Inventory_Kamera)
and Sonar result first. Fix a source failure through a new PR; the next successful
merge gets its own version. Skipped versions from failed commits are expected.
For a transient service failure, rerun the failed CircleCI build on the same SHA,
then rerun the release workflow.

The [Release workflow](https://github.com/rogerioth/Inventory_Kamera/actions/workflows/release.yml)
offers **Run workflow** on `master`. Leave its optional `commit` input empty to
release the current `master` commit, or supply a full 40-character SHA from the
`master` first-parent history. The selected commit must still pass the same CI
verification. The CLI equivalent is:

```powershell
gh workflow run release.yml --ref master
gh workflow run release.yml --ref master -f commit=<full-master-commit-sha>
```

Replace the placeholder before running the second command. Dispatching a historical
commit does not bypass CI or change its calculated version.

Reruns use the same tag for the same source SHA. An incomplete draft can be
resumed; an already published release is left intact. Published assets and tags
must not be overwritten or moved. To change a released binary, merge a fix and
publish the next version instead. A tag/version collision with a different source
SHA is an error requiring maintainer investigation.
