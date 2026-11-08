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

Requires the .NET 10 SDK or runtime on the machine you run `lanq` from, plus `git` and `ssh` on the PATH.

## Quick start

1. List your machines in a private `machines.yml` (start from the [example](https://github.com/jangolath/lanqueue/blob/main/machines.example.yml)) and make sure key-based SSH login works.
2. Check the fleet: `lanq health`
3. Run a job from inside a git repository: `lanq run --needs dotnet,linux -- dotnet test -c Release`

`lanq` picks the least loaded machine that has what the job needs, ships your current git state (including
uncommitted changes) to a throw-away worktree, runs the one allowlisted command, streams the log back and cleans up.

## What a worker needs

An SSH server, the toolchain your jobs use, and git (recommended, the default) or, with `transfer: archive`, just
`tar` (built into Windows 10 1803+ and Linux). Nothing from lanqueue is installed on it, and it needs no GitHub
access. Setup steps for Linux and Windows: [docs/WORKERS.md](https://github.com/jangolath/lanqueue/blob/main/docs/WORKERS.md).

## Safe by default

- Key-only SSH with strict host-key checking; there is no password option.
- Only commands and directories you allowlist can run; arguments are quoted, never interpreted by a shell.
- Best-effort secret scan of the command and uncommitted changes before anything leaves your machine.
- No daemon, no telemetry, and no network access except the SSH hosts you list.

## More

- [Full README](https://github.com/jangolath/lanqueue/blob/main/README.md)
- [Security model and limits](https://github.com/jangolath/lanqueue/blob/main/SECURITY.md)
- [Using it with coding agents](https://github.com/jangolath/lanqueue/tree/main/docs/agents)
- [Releasing](https://github.com/jangolath/lanqueue/blob/main/RELEASING.md)
- [Changelog](https://github.com/jangolath/lanqueue/blob/main/CHANGELOG.md)
- [Release notes](https://github.com/jangolath/lanqueue/releases)

MIT licensed.
