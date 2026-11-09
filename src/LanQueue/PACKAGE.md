![LanQueue - your spare machines, back to work](https://raw.githubusercontent.com/jangolath/lanqueue/main/assets/lanqueue-banner.png)

# LanQueue

**RAM and new hardware are expensive. Your old PCs are not. Plug them back in and give them work.**

`lanq` is a small .NET global tool that sends builds, tests, log analysis and headless coding-agent jobs to the
other computers you already own. Your old machines can still help you get development done: all a worker needs is a
LAN connection, SSH, git and the toolchain for your jobs, and it joins your queue. No git on the old PC? Set
`transfer: archive` and it needs only SSH, tar and your toolchain. Your dev PC stays free for writing code.

## Install

```
dotnet tool install --global LanQueue
dotnet tool update --global LanQueue
```

Requires the .NET 10 SDK on the machine you run `lanq` from, plus `git` and `ssh` on the PATH.

No .NET SDK? Each GitHub release also has a single-file native `lanq` for Windows (x64, Arm64), Linux (x64, Arm64,
glibc 2.27 and later) and macOS (Apple silicon), with `SHA256SUMS`, build-provenance attestations and a Scoop
manifest: https://github.com/jangolath/lanqueue#install

## Quick start

1. Write a starter `machines.yml` with `lanq init` (or start from the [example](https://github.com/jangolath/lanqueue/blob/main/machines.example.yml)), list your machines, and make sure key-based SSH login works.
2. Check your setup and the fleet: `lanq doctor --remote`, then `lanq health`
3. Run a job from inside a git repository: `lanq run --needs dotnet,linux -- dotnet test -c Release`
4. Bring results back: add `--fetch 'TestResults/**/*.trx'` (repeatable) and the matching files land in
   `.lanq/artifacts/<job id>/`, whether the job passed or failed.

Onboarding a PC: `lanq makeready --ssh user@old-pc.lan --os windows` runs the read-only
[lanq-makeready](https://github.com/jangolath/lanq-makeready) check over SSH (built in, sent on stdin, nothing written to
the PC) and suggests a `machines.yml` entry; `lanq makeready --all` checks every configured machine and prints one
summary table (`--json` for one object). `lanq unlock <machine>` clears slots left by a crashed run. `lanq health
--json` and `lanq run --json` print machine-readable results for scripts and coding agents.

`lanq` picks the least loaded machine that has what the job needs, ships your current git state (including
uncommitted changes) to a throw-away worktree, runs the one allowlisted command, streams the log back and cleans up.

`lanq`'s exit code is the job's own exit code; `2` means rejected by policy or bad arguments, `70` an infrastructure
error, `75` no eligible machine and `124` that the job ran past `--timeout <minutes>` (default 240) and was stopped.
A job that itself exits with 124 looks the same; the `[lanq] TimedOut: ...` status line on stderr tells them apart.

Setting up a pool for a team (one orchestrator, Windows 10/11 and Linux workers, low-RAM micro PCs, overnight family
PCs)? Follow the [team quick start](https://github.com/jangolath/lanqueue/blob/main/docs/TEAM-QUICKSTART.md).

## What a worker needs

An SSH server, the toolchain your jobs use, and git (recommended, the default) or, with `transfer: archive`, just
`tar` (the built-in bsdtar on Windows 10 1803+; GNU tar or bsdtar plus gzip on Linux, busybox tar is refused). Nothing from lanqueue is installed on it, and it needs no GitHub
access. Setup steps for Linux and Windows: [docs/WORKERS.md](https://github.com/jangolath/lanqueue/blob/main/docs/WORKERS.md).

## Lend it a shared or old PC

- `allowed_hours` per machine, by day: `["Mon-Fri 22:00-07:00", "Sat,Sun 01:00-09:00"]`, on the worker's own clock.
  A job still running when the window ends is stopped after a grace period (exit 124, `[lanq] WindowClosed`).
- Optional Wake-on-LAN per machine (`wake: {mac: "aa:bb:cc:dd:ee:ff"}`): `lanq run` wakes one sleeping machine only when no awake
  one can take the job (needs `--wait`, `--no-wake` turns it off), `lanq health --wake` wakes sleepers, both only inside `allowed_hours` with at least `min_window_minutes` + `wait_seconds` of the window left; `lanq wake <machine> [--wait] [--json]` wakes it on request. Needs
  Wake-on-LAN enabled in the BIOS/UEFI and the NIC driver, usually wired Ethernet; see docs/WORKERS.md.
- A `PAUSE` file (Linux: `PAUSE` or `pause`) in the worker's `work_root` holds the machine; anyone at that PC can create
  or delete it. A job already running is not affected.
- Jobs run at low priority by default (`nice`/`ionice` on Linux, BelowNormal on Windows); `priority: normal` turns it off.
- Machines with little free memory are held (`min_free_mem_gb`, Linux memory pressure), and `lanq run --mem 4` asks
  for a machine with at least 4 GB available.
- Per-machine `env:` (for example `NODE_OPTIONS: "--max-old-space-size=2048"`) and `path_prepend:` (for nvm, pyenv,
  rustup or sdkman tools a non-login SSH session cannot find).

## Split long work across the pool

`lanq run --shards 4 -- npx jest --shard={shard}/{shards}` runs four pieces at once on free slots (`--shards auto`
uses every free slot that can take the job); each gets `{shard}`/`{shards}` in its arguments and `LANQ_SHARD`/
`LANQ_SHARDS` in its environment, and the snapshot is built once. Exit 0 only if every shard passed; `--json` prints
one object with a `shards` array. `lanq health` shows cores, total RAM, free slots and what each machine suits.
Recipes for .NET, Node, Python, Go and Rust:
[docs/SHARDING.md](https://github.com/jangolath/lanqueue/blob/main/docs/SHARDING.md).

## Safe by default

- Key-only SSH with strict host-key checking; there is no password option.
- Only commands and directories you allowlist can run; arguments are quoted, never interpreted by a shell (Windows
  workers never take arguments containing `"` `&` `|` `<` `>` `^` `%`, which `cmd.exe` shims would re-parse).
- Worker output is untrusted: control, bidi and zero-width characters are shown as `?`.
- `--fetch` treats the worker as untrusted: regular files only (links never followed), sent as checksummed text (no
  archive is ever extracted), every path, size, count and hash checked again locally, files created new below
  `.lanq/artifacts/<job id>/` and never through a link. Fetch problems are reported but never change the exit code.
- Best-effort secret scan of the command and uncommitted changes, plus the names of committed files (`.env`,
  `id_rsa`, `*.pem` and similar) and the content of `.npmrc`, `.pypirc` and `.yarnrc.yml`, before anything leaves your
  machine.
- On git workers, git runs with hooks off, transports refused and the system and global git config ignored (git 2.32+),
  and a tampered cache is recreated; later jobs send only new commits.
- No daemon, no telemetry, and no network access except the SSH hosts you list.

## More

- [Full README](https://github.com/jangolath/lanqueue/blob/main/README.md)
- [Security model and limits](https://github.com/jangolath/lanqueue/blob/main/SECURITY.md)
- [Using it with coding agents](https://github.com/jangolath/lanqueue/tree/main/docs/agents)
- [Releasing](https://github.com/jangolath/lanqueue/blob/main/RELEASING.md)
- [Changelog](https://github.com/jangolath/lanqueue/blob/main/CHANGELOG.md)
- [Release notes](https://github.com/jangolath/lanqueue/releases)

MIT licensed.
