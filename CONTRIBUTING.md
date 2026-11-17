Contributing
============

Releasing
---------

This section is for maintainers.

[release-please](https://github.com/googleapis/release-please) manages the version, `CHANGELOG.md`,
tags and GitHub releases; nothing is bumped by hand.

**What gets released.** PRs are squash-merged, so the PR title is the commit release-please reads
and must follow [Conventional Commits](https://www.conventionalcommits.org) (CI checks this).
`fix:` releases a patch, `feat:` a minor and `feat!:` or a `BREAKING CHANGE:` footer a major
version. Other types release nothing.

**Releasing.**

1. After each merge to `main`, release-please opens or updates a release PR that bumps the version in
   `GrowthBook/GrowthBook.csproj` and adds a `CHANGELOG.md` entry. Edit the changelog wording there if
   needed.
2. Merging the release PR creates the `vX.Y.Z` tag and GitHub release, then the **Release** workflow
   (`build-and-deploy.yml`) tests, packs and publishes the package to NuGet.
3. If the publish fails, fix the cause and run **Release** from the Actions tab on the release tag.
   Versions already on NuGet are skipped.
