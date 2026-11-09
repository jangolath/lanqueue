# Releasing

Releases are tag-driven and publish to nuget.org with NuGet Trusted Publishing (GitHub OIDC). No long-lived API key is stored anywhere.

## One-time owner setup

1. Create a nuget.org account. Note your **profile name** (username), not your email.
2. On nuget.org: click your username, choose **Trusted Publishing**, add a policy with these values (case-insensitive):
   - Repository Owner: `jangolath`
   - Repository: `lanqueue`
   - Workflow File: `release.yml` (file name only)
   - Environment: `nuget-release`
   - Scopes: allow publishing new packages and new versions; package glob `LanQueue`.

   A policy for a private repo starts as a 7-day temporary policy and becomes permanent after the first successful publish.
3. In GitHub: Settings > Secrets and variables > Actions > **Variables**, add repository variable `NUGET_USER` = your nuget.org profile name. It is not a secret.
4. In GitHub: Settings > Environments, create `nuget-release` (the workflow creates it on first run if missing). Optional: add yourself as a required reviewer so each publish needs a click.
5. Optional: reserve the ID prefix `LanQueue` on nuget.org (Account > Manage Packages / reserved prefixes request).

## Cutting a release

1. Make sure `main` is green.
2. Tag and push: `git tag v0.2.0 && git push origin v0.2.0` (the version is the tag without `v`).
3. Watch the `release` run in the Actions tab. It builds with warnings as errors, tests, packs, attests the `.nupkg`'s build provenance (`attest` job, the only one with `id-token: write` and `attestations: write`), then (after any environment approval) pushes to nuget.org with `--skip-duplicate` and creates a GitHub Release with the attested `.nupkg` attached. Check it with `gh attestation verify LanQueue.<version>.nupkg --repo jangolath/lanqueue` (see "Verify a release" in the README).

## Dependencies

Every project commits a `packages.lock.json`, and restores on CI (`CI=true`, which GitHub Actions sets) run in locked mode, so a build fails if the resolved packages drift. After changing a package reference run `dotnet restore --force-evaluate` and commit the updated lock files. Dependabot (`.github/dependabot.yml`) proposes grouped weekly updates for NuGet packages and GitHub Actions; actions stay pinned to full commit SHAs.

Once a release is indexed, users install it with `dotnet tool install --global LanQueue` and upgrade with `dotnet tool update --global LanQueue`. The package page uses the dedicated `src/LanQueue/PACKAGE.md` (NuGet renders limited Markdown: no mermaid, HTML or relative links), so keep its links absolute and update it when the README changes materially.

To rehearse, run the `release` workflow manually (workflow_dispatch); it builds, tests and packs but never publishes.
