# Repository review policy

Contributor changes to `master` require an approving review from @rogerioth.
[CODEOWNERS](../.github/CODEOWNERS) assigns every file, including CODEOWNERS
itself, to that account. New reviewable changes dismiss earlier approvals.

The [ruleset definition](../.github/rulesets/owner-approval.json) requires a pull
request, one approval, and code-owner approval. Its only bypass actor is
@rogerioth (GitHub user ID `9361619`), with bypass limited to pull-request merges.
This lets the maintainer merge their own pull requests without self-approval.
It does not grant other collaborators a bypass or permit direct pushes through
the ruleset.

GitHub's bypass permission applies to the merging account, not the pull-request
author. The maintainer can therefore also intentionally override another
contributor's pull request; the contribution policy reserves this exception for
the maintainer's own changes.

## Applying and verifying the settings

Committing the JSON does not activate GitHub protection. An authenticated owner
must apply it under **Settings → Rules → Rulesets**, or through GitHub's
[repository ruleset API](https://docs.github.com/en/rest/repos/rules#create-a-repository-ruleset).
Update the existing rule with this name when present rather than creating a
duplicate, and preserve unrelated protections.

Verify that the live rule is active, targets `master`, requires code-owner
approval, dismisses stale approvals, and lists only @rogerioth for pull-request
bypass. CODEOWNERS must be present on the PR's base branch before its ownership
requirements can take effect. Keep the definition and live settings in sync.

See GitHub's documentation on
[code-owner enforcement](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-code-owners)
and [ruleset bypass permissions](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/creating-rulesets-for-a-repository#granting-bypass-permissions-for-your-branch-or-tag-ruleset).
