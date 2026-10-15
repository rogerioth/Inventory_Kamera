# Contributing

Keep contributions focused on a clear problem and explain the resulting behavior.
Check existing issues and pull requests before starting overlapping work.

See [Development](docs/development.md) for prerequisites, builds, code locations,
logging, and the available offline regression harnesses.

## Pull requests

- Target pull requests at `master`.
- Use descriptive `feature/<name>` or `fix/<name>` branches.
- Describe the problem, what changed, and how to reproduce or recognize the fix.
- Include the validation commands and results, with any checks you could not run.
- Keep unrelated changes separate and update relevant documentation when behavior changes.

Each merge into `master` is a release candidate and publishes automatically after
the merge commit passes CI. Document user-visible changes in the PR description;
GitHub uses merged PRs to generate release notes. Do not create a version-bump
commit or upload a replacement executable manually. See
[Releases and downloads](docs/releases.md) for versioning and release recovery.

For OCR or navigation changes, explain which screen or text evidence supports the
result and how uncertain reads, interrupted navigation, and cancellation behave.
Saved captures and a focused regression case are useful when they demonstrate the bug.

## Validation

Run the smallest relevant offline checks from `tests/`, using saved captures when
available. Build the affected Debug targets for application changes; check both
`x64` and `Any CPU` when the change applies to both. Documentation-only changes
do not require a build.

The full CI baseline can also be run locally with `.\tests\run-ci.ps1`.
See [Continuous integration](docs/continuous-integration.md) for its checks,
service settings, and analysis limits.

A live game session or particular hardware is not required to contribute.
Automated regression checks must run offline, without launching the app or
sending input to a live game. Use saved screenshots and injected navigation
callbacks to exercise recognition and recovery behavior.

Report failures and environment limitations alongside successful checks rather
than presenting unrun validation as complete.

## Diagnostic evidence

Include relevant log excerpts and saved failure screenshots when reporting a scan
problem. State the game language, window resolution, and any settings relevant to
the failure. Include the expected and actual result.

Before sharing logs, screenshots, or exports, remove the UID, account names,
private local paths, and other personal information. Share only the captures and
data needed to explain or reproduce the problem; avoid uploading an entire
unredacted logging folder.

## Review and merging

Contributor pull requests require approval from **@rogerioth** before merging
into `master`. The repository's [CODEOWNERS](.github/CODEOWNERS) designates
@rogerioth as the sole owner for all files.

New changes after approval require renewed approval before merging. Resolve
review feedback and report any additional validation with the updated change.

The maintainer, @rogerioth, may bypass the review requirement for their own
pull requests.

See [Repository review policy](docs/repository-governance.md) for the ruleset
definition and maintainer setup details.
