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
3. Watch the `release` run in the Actions tab. In order:
   - `pack` builds with warnings as errors, runs the tests and packs the `.nupkg`;
   - `native` publishes one NativeAOT binary per target (win-x64 and win-arm64 on Windows, osx-arm64 on macOS, linux-x64
     and linux-arm64 inside Microsoft's .NET 10 cross-build images against their glibc 2.27 sysroot), packages it and
     smoke-tests it where the runner can run it (`scripts/smoke-native.sh`);
   - `smoke` runs the Linux binaries inside Ubuntu 18.04 (x64 and on an arm64 runner) and the win-arm64 binary on a
     Windows arm64 runner;
   - `assemble` writes `SHA256SUMS` over every asset and the Scoop manifest `lanq.json` (`scripts/scoop-manifest.sh`);
   - `attest` (the only job with `id-token: write` and `attestations: write`) attests every asset;
   - `publish` (after any `nuget-release` approval) creates a **draft** GitHub Release with every asset, pushes the
     package to nuget.org with `--skip-duplicate`, then makes the release public.

   If anything fails before `publish`, nothing is published. If `publish` fails part-way, re-run it: it reuses its own
   draft (replacing the assets), and the NuGet push skips a version that is already there. Check an asset with
   `gh attestation verify <file> --repo jangolath/lanqueue` (see "Verify a release" in the README).

To rehearse, run the `release` workflow manually (workflow_dispatch): it runs every job up to `assemble`, so every
binary is built and smoke-tested, and never attests or publishes.

## Native binaries and the pinned runtime

The native binaries are built only by `dotnet publish -p:LanqNative=true` (see `src/LanQueue/LanQueue.csproj`); build
and pack never see those settings, so the NuGet tool stays framework-dependent (a pack with `LanqNative` set fails on
purpose). They restore in locked mode against `src/LanQueue/packages.native.lock.json`, which covers all five RIDs.

`LanqRuntimePackVersion` in the project pins the .NET runtime patch compiled into the binaries (ILCompiler and the
runtime pack) and the trim/AOT analyzer package (ILLink) used by every build, so lock files do not change with the SDK
patch a machine has. Bump it when .NET ships a patch, especially a security one (the binaries carry their own
runtime): set the new `10.0.x`, then run `dotnet restore --force-evaluate` and
`dotnet restore src/LanQueue -p:LanqNative=true --force-evaluate`, run the tests, and commit the project and both lock
files together. Dependabot does not see these implicit packages.

## Updating the embedded lanq-makeready

`lanq makeready` embeds the two scripts of a [lanq-makeready](https://github.com/jangolath/lanq-makeready) release in
`src/LanQueue/Makeready/` (byte-identical: `.gitattributes` marks them `-text`). Never edit them by hand. To move to a
new release, run `scripts/sync-makeready.sh v0.2.0` (needs `gh` and `sha256sum`): it downloads the release, checks both
files with `sha256sum -c` against its `SHA256SUMS` and with `gh attestation verify`, and only then copies them in. Then
set the two pinned hashes in `tests/LanQueue.Tests/MakereadyTests.cs` and `Cli.MakereadyVersion`, check that the
report schema is still `lanq-makeready/v1`, run the tests, and commit scripts and pins together.

## Dependencies

Every project commits a `packages.lock.json`, and restores on CI (`CI=true`, which GitHub Actions sets) run in locked mode, so a build fails if the resolved packages drift. After changing a package reference run `dotnet restore --force-evaluate` and commit the updated lock files. Dependabot (`.github/dependabot.yml`) proposes grouped weekly updates for NuGet packages and GitHub Actions; actions stay pinned to full commit SHAs.

Once a release is indexed, users install it with `dotnet tool install --global LanQueue` and upgrade with `dotnet tool update --global LanQueue`; native-binary users download the new archive, and Scoop users run `scoop update lanq`. The package page uses the dedicated `src/LanQueue/PACKAGE.md` (NuGet renders limited Markdown: no mermaid, HTML or relative links), so keep its links absolute and update it when the README changes materially.
