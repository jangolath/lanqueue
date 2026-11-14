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

   **1b. Smoke the release candidate on real workers.** Push the release branch, run the release dry run on it, then
   the smoke with that run's Windows binary, and paste its table into the release PR:

   ```powershell
   gh workflow run release.yml --ref <release branch>
   gh run list --workflow release.yml --branch <release branch> --limit 1   # wait for it; note its id
   pwsh scripts/smoke-workers.ps1 -Run <run id> -Branch <release branch> -Config "$env:APPDATA\lanq\machines.smoke.yml"   # or powershell -File
   ```

   `-Branch` is required and may not be the default branch. The run must be a successful `release` workflow_dispatch
   run on that branch whose `headSha` is the branch head on `origin`, so it smokes exactly the commit being released.
   The script downloads and unpacks the run's `native-win-x64` artifact, and the binary's `lanq --version` must exit 0
   and carry `+<headSha>` (the dry run's version is `0.0.0-dryrun.N+<sha>`). It never runs the installed `lanq`.

   It refuses your real config: `LANQ_CONFIG`'s file and the per-user `machines.yml`, by resolved path (links followed)
   or by the same SHA-256 (a copy or hard link), and checks they are unchanged at the end. It runs with a temporary
   `LANQ_HOME` (restored afterwards with `LANQ_CONFIG` and the git identity variables), and refuses unless each smoke
   machine's `ssh`, `work_root` and `max_jobs` equal the real config's (so its slot locks are the ones real jobs use).
   Every smoke job has `--timeout 5`, and every lanq call is stopped after 15 minutes. It ends with `lanq unlock` on each
   smoke machine and a check, by a job that must see its own slot, that no slot of a smoke job is left. Scenario 8 (from
   0.10.0) checks that `--plan-timeout 1` ends a plan `TimedOut` (124) with its job cleaned up, that `--plan --on` puts
   every job on the machine named, and that a narrowing which leaves a job no machine is `NoCapacity` (75) with a rerun
   `--only`; an older binary gets N/A there. Scenario 10 (from 0.11.0) checks test results: `--results junit:...` must
   count a fixed file (two tests, `Smoke.Fails` failed), and a run that writes no file must be `Completed` 0 with
   `noTests` and an `ask_developer` step; a binary before 0.11.0 gets N/A there (and any other answer to the probe is a
   FAIL).

   It prints a PASS/FAIL/SKIP/N/A table and **exits 0 only when every scenario that applies passed**. N/A means the
   smoke config has no machine of that kind (a Windows-only pool has no Linux scenarios; no machine with `max_jobs` of
   2 or more, no exclusive scenario), and never fails. A configured machine that is not reachable, health that fails,
   or a scenario that could have run but was skipped is a failure; `-AllowSkip` accepts skips.

   The smoke config is yours and lives outside every repository: `machines.smoke.yml` in the same per-user folder as `machines.yml`: `%APPDATA%\lanq\machines.smoke.yml` on Windows, `~/.config/lanq/machines.smoke.yml` (or `$XDG_CONFIG_HOME/lanq/machines.smoke.yml`) elsewhere. (The repository's
   `machines.example.yml` is only a template; lanq never reads it.) It holds the same machines as `machines.yml`
   (copy their entries unchanged) and this policy:

   ```yaml
   # machines.smoke.yml: only for scripts/smoke-workers.ps1 (RELEASING.md step 1b). The machines below must be copies of
   # the entries in machines.yml: the same name, ssh, work_root and max_jobs (the script refuses any difference).
   policy:
     allowed_commands:
       - "sh smoke/check.sh"
       - "powershell -NoProfile -ExecutionPolicy Bypass -File smoke/check.ps1"
     allowed_directories: ["."]
     allowed_env: [SMOKE_VALUE]
   machines:
     # - name: build-01
     #   os: linux
     #   ssh: build@10.20.30.11
     #   work_root: /srv/lanq
     #   capabilities: [dotnet, linux]
     #   max_jobs: 2
   ```

   List every pool machine you want covered: each OS present gets its scenarios, and one with `max_jobs` of 2 or more
   gets the exclusive scenario. An agent runs the smoke only when the developer asks (the smoke's own `lanq unlock` is
   then part of it), and never edits `machines.yml` or `machines.smoke.yml`.
2. Date the release's section in `CHANGELOG.md`: the heading must be `## X.Y.Z (YYYY-MM-DD)`
   (for example `## 0.6.0 (2026-11-10)`, not `## 0.6.0 (unreleased)`) with the release's notes under it, merged to
   `main` before tagging.
   `scripts/release-notes.sh` takes that section's text (up to the next level-two heading) as the release notes, and the
   `pack` job fails for a stable tag whose section is missing, empty or still says "unreleased". A prerelease tag
   (`v0.6.0-rc.1`) uses a `## 0.6.0-rc.1` section if it has one, else the `## 0.6.0` section, dated or not; a manual run
   uses the first section that is not `## Unreleased`. The notes, followed by a link to the full `CHANGELOG.md`, appear
   on the package's **Release Notes** tab on nuget.org and at the top of the GitHub release body, above the "Full
   Changelog" compare link GitHub generates. Fix a typo after release by editing the GitHub release; the nuget.org notes
   are part of the package and change only with a new version.
3. Tag and push: `git tag v0.2.0 && git push origin v0.2.0` (the version is the tag without `v`). The tag must be
   `v` + strict semver (`scripts/release-version.sh` refuses anything else, including build metadata such as `+abc`). A
   tag with a pre-release part, such as `v0.5.0-rc.1`, becomes a GitHub **prerelease** that is never marked latest, so
   `releases/latest` (and the Scoop URL) keep serving the last stable release; nuget.org lists it as a prerelease too.
4. Watch the `release` run in the Actions tab. In order:
   - `pack` checks the runtime pin against the SDK's runtime, validates and classifies the version, builds with
     warnings as errors, runs the tests and packs the `.nupkg` with the release notes (and checks they are in it);
   - `native` publishes one NativeAOT binary per target (win-x64 and win-arm64 on Windows, osx-arm64 on macOS, linux-x64
     and linux-arm64 inside Microsoft's .NET 10 cross-build images against their glibc 2.27 sysroot), packages it and
     smoke-tests it where the runner can run it (`scripts/smoke-native.sh`);
   - `smoke` runs the Linux binaries inside Ubuntu 18.04 (x64 and on an arm64 runner) and the win-arm64 binary on a
     Windows arm64 runner;
   - `assemble` writes `SHA256SUMS` over every asset and the Scoop manifest `lanq.json` (`scripts/scoop-manifest.sh`);
   - `attest` (the only job with `id-token: write` and `attestations: write`) attests every asset;
   - `publish` (after any `nuget-release` approval) creates a **draft** GitHub Release with every asset, pushes the
     package to nuget.org with `--skip-duplicate`, then makes the release public.

   If anything fails before `publish`, nothing is published. After any failure use **Re-run failed jobs**, never
   **Re-run all jobs**: re-running everything would rebuild every binary, and the new build's `SHA256SUMS` would no
   longer match a draft the first attempt left, so `publish` would refuse it. `publish` (`scripts/release-draft.sh`)
   reuses an existing draft only when the draft is empty or holds the same `SHA256SUMS` as the assets being published,
   and never touches a published release; the NuGet push skips a version that is already there. If it refuses a stale
   draft, check it, delete it (`gh release delete <tag> --repo jangolath/lanqueue`, which keeps the tag) and re-run.
   The same applies to a draft left **without** `SHA256SUMS` (an upload that stopped part-way): the job cannot tell
   whose assets it holds and refuses it, so delete that draft, then use **Re-run failed jobs**; `publish` creates a fresh
   draft from the same build's assets.
   A stable release is marked latest only when it is newer than every stable release already published, so a backport
   such as `v0.4.1` after `v0.5.0` stays off `releases/latest` (and its `lanq.json` off the Scoop latest URL).
   Runs for the same tag are queued (workflow `concurrency`), never cancelled. Check an asset with
   `gh attestation verify <file> --repo jangolath/lanqueue` (see "Verify a release" in the README).

To rehearse, run the `release` workflow manually (workflow_dispatch, which takes no inputs): it runs every job up to
`assemble`, so every binary is built and smoke-tested, and never attests or publishes. Publishing is gated on a tag
push alone (the `attest` and `publish` jobs' `if:`), so there is no dry-run switch to set or forget.

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

Nothing lets the pin fall behind silently: `scripts/check-runtime-pin.sh` fails the release's `pack` job and the CI
`aot-linux` job when the SDK they install carries a newer `Microsoft.NETCore.App` 10.0 patch than the pin, and the weekly
`runtime-pin` workflow fails when Microsoft's `releases.json` lists one. So when .NET ships a patch, CI turns red until the
pin is bumped. Checklist for a bump:

1. Set `LanqRuntimePackVersion` to the new `10.0.x`; refresh both lock files as above.
2. Refresh the digest-pinned images in `.github/workflows/release.yml` (Dependabot cannot): the two .NET cross-build
   images and the Ubuntu 18.04 smoke image. Get the current digest with
   `docker buildx imagetools inspect <image>:<tag>` and replace the `@sha256:` part. The weekly `runtime-pin` run prints
   a notice for each pinned image that has a newer digest.
3. Run the tests and a manual `release` run (workflow_dispatch) to build and smoke-test every binary, then commit
   everything together.

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
