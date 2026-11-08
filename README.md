# LanQueue

**Put the spare machines on your own network to work, without giving up your desk.**

`lanq` is a small .NET global tool that sends builds, tests, log analysis and headless coding-agent jobs to the
other computers you already own. It checks which ones are awake and not busy, picks the least loaded machine that
has what the job needs, ships your current git state (HEAD plus uncommitted changes) to a throw-away worktree over
key-only SSH, runs one allowlisted command, streams the log back, cleans up, and records how long it took.

It is headless and scriptable, it has no daemon and no server to install on the remote machines (just `ssh`, `git`
and a shell), and it sends nothing anywhere except to the hosts you list. There is no telemetry.

## Why this exists

This is the author's first-hand account, not a benchmark. The author works on two projects (a .NET API and a web
front end) from one dev PC. One overnight run put two heavy agent-driven build/test jobs, the local dev stack and a
scheduled task on that single machine at the same time. The local API crashed, the front-end dev server broke, a
task expected to take an hour or two took most of an afternoon, and the failures that night were machine load, not
code defects.

The fix was organisational before it was technical: allow one heavy job at a time on the dev PC, and send builds
and tests to two spare machines on the LAN (a build box and a small-form-factor PC) over key-only SSH, so the dev PC
stays free for writing code and orchestrating. Along the way the author hit the usual remote-Windows traps (stale
incremental builds after file overlays, "Filename too long" in deep worktrees, quirks of running tests over SSH).
`lanq` is that routine turned into a tool.

`lanq stats` records wall-clock time per job in a local log. There are no published measurements yet, and this README
makes no speed-up claim.

## Install

Requires the .NET 10 SDK or runtime on the machine you run `lanq` from, and `git` and `ssh`/`scp` on the PATH.
Remote machines need `git` and an SSH server (Linux: `sh`; Windows: OpenSSH Server with PowerShell 5.1+).

```bash
dotnet tool install --global LanQueue      # once published to NuGet
# until then, from a clone:
dotnet pack src/LanQueue -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg LanQueue
```

## Quick start

1. Copy `machines.example.yml` to a private location (for example `~/.config/lanq/machines.yml`, or
   `%APPDATA%\lanq\machines.yml` on Windows) and edit it. A config in the current directory is never picked up
   implicitly; pass `--config FILE` or set `LANQ_CONFIG`.
2. Make sure key-based login works and the host key is already in `known_hosts`:
   `ssh -i "$LANQ_KEY_BUILD01" user@build-01.lan true`
3. Check the fleet:

   ```
   $ lanq health
   build-01       OK    load 0.12  disk free 180.4 GB  slots 0/2
   build-02       HOLD  load 0.05  disk free 90.1 GB  slots 0/1 (outside allowed hours)
   ```

4. Run something from inside a git repository:

   ```bash
   lanq run --needs dotnet,linux --dir tests -- dotnet test -c Release
   ```

   The job's exit code becomes `lanq`'s exit code. `--wait 120` keeps retrying for up to 120 seconds when every
   machine is busy or on hold. `lanq stats` sums wall-clock time from your local log.

Exit codes: the job's own code; `2` rejected by policy or bad arguments; `70` infrastructure error (SSH, git);
`75` no eligible machine.

## How a job runs

1. The command and directory are checked against your policy. Rejected jobs never touch the network.
2. HEAD plus uncommitted and untracked, non-ignored files are snapshotted into a commit using a temporary index
   (your index, branches and working tree are untouched) and bundled. The overlay is scanned for secrets.
3. Every machine is probed; those that are unreachable, over the load threshold, outside their hours, full or
   missing a required capability are skipped. The rest are ranked by load.
4. A slot file is claimed atomically on the chosen machine (`max_jobs` per machine), the bundle is uploaded with
   `scp`, fetched into a bare cache repo, and a detached `git worktree` is created for the job.
5. The command runs in the worktree; output streams to your terminal and to `<state>/logs/<job>.log`, where
   `<state>` is `$LANQ_HOME` or a per-user folder (`%LOCALAPPDATA%\lanq`, or `~/.local/state/lanq`), never your repo.
6. The worktree, ref, bundle and slot are removed, even if the job failed. Wall-clock time is appended to
   `<state>/jobs.jsonl`. Any `.lanq/` folder in the repository is excluded from the snapshot.

## Configuration reference

```yaml
policy:
  load_threshold: 0.80        # (0,1]; load average per core (Linux) or CPU% (Windows) above this = hold short
  min_free_disk_gb: 5         # a machine with less free space on its work_root drive is held (default 5)
  allowed_commands: [...]     # argv must START with one of these token sequences, e.g. "dotnet test"
  allowed_directories: [...]  # relative to the repo root; "src" also allows "src/App"; "." allows only the root
machines:
  - name: build-01            # letters, digits, - _
    os: linux                 # linux | windows
    ssh: user@host            # no leading "-"
    key_env: ENV_VAR_NAME     # env var holding the path of the private key file (optional; else ssh defaults)
    work_root: /srv/lanq      # Linux: absolute path. Windows: a drive path such as D:/lanq. Forward slashes, no spaces, no ".."
    capabilities: [dotnet]    # tags matched by --needs (all must be present)
    max_jobs: 2               # concurrent jobs via slot files
    allowed_hours: "22:00-07:00"   # optional; local time of the machine running lanq; may wrap midnight
```

Unknown keys are an error (so a `password:` field is refused rather than ignored). Config lookup: `--config`,
then `$LANQ_CONFIG`, then the per-user config folder (`~/.config/lanq/machines.yml` or `%APPDATA%\lanq\machines.yml`).
`LANQ_HOME` moves the state folder (job log, logs, scratch); it defaults to a per-user folder outside any repository.

## Security model

Each default below is enforced in code and covered by a unit test.

- **Key-only SSH.** Every ssh/scp call uses `BatchMode=yes`, `PasswordAuthentication=no`,
  `KbdInteractiveAuthentication=no`, `PreferredAuthentications=publickey` and `StrictHostKeyChecking=yes`. There is
  no password option anywhere in the config.
- **Command allowlist.** Only argv beginning with a configured token sequence runs. Each argument is individually
  quoted for the remote shell, so `dotnet test; rm -rf /` as one argument is neither allowed nor interpreted.
- **Directory allowlist.** Jobs run only in allowlisted, relative directories inside the job worktree; `..`,
  absolute and drive-letter paths are refused.
- **Best-effort secret scan.** The command line and the uncommitted overlay are scanned for private-key blocks,
  AWS/GitHub/Slack/`sk-` tokens, JWTs, bearer tokens, and password/secret/API-key style assignments (quoted, JSON,
  YAML, `.env` and `Password=...;` connection strings). Files named like `.env`, `*.pem`, `*.pfx`, `*.jks`,
  `id_rsa`, `*.tfvars`, `terraform.tfstate`, `.git-credentials`, `.aws/credentials` or `*.local.yml` are refused by
  name. A hit refuses the job before anything is sent, and the message never echoes the value. If a scan pattern
  times out the job is refused (fail closed). This is a safety net, not a guarantee: it does not scan committed
  history or binary files, and it cannot recognise every secret format.
- **No permission-skipping agent flags.** Any argument containing `dangerously`, `skip-permissions`,
  `bypassPermissions`, `--yolo`, `bypass-approvals`, `danger-full-access`, `approval_policy=never` or
  `--ask-for-approval never` is rejected, even for an allowlisted command.
- **Isolation.** One detached worktree per job, removed afterwards. Gitignored files are never shipped.
- **No telemetry**, no network access except the SSH hosts you configure.

See [SECURITY.md](SECURITY.md) for what this does not protect against.

## Limitations (v0.1)

- The secret scan is pattern-based and best effort; it does not scan already-committed history or file contents
  of binary files. It is a safety net, not a guarantee.
- The allowlist limits what `lanq` will start, not what an allowlisted program does. Build and package tools such as
  `dotnet test`, `dotnet build`, `npm test` and `npm ci` execute project code (test code, MSBuild targets, npm
  lifecycle scripts) with the remote user's rights. Only run code you trust, and use a dedicated low-privilege
  account on the remote machines.
- Cancellation and timeouts: cleanup kills the job's process group (Linux) or PowerShell process tree (Windows)
  recorded in `locks/job-<id>.pid` before removing the worktree. This is best effort: it needs the machine to be
  reachable when cleanup runs (if the network is down, the job may keep running until it ends on its own), and a
  job that deliberately detaches into a new session or process group can escape it.
- Windows remote commands are passed as an encoded command line limited to roughly 8000 characters; very long
  argument lists are refused with a clear message.
- The full HEAD history is bundled on every run (no incremental transfer yet).
- A crashed orchestrator can leave a slot file behind; delete `locks/slot-N` (and any stale `locks/job-*.pid`) under
  the machine's `work_root`.
- No persistent queue: `--wait` polls every 15 seconds. Windows arguments containing embedded double quotes can
  be mangled by PowerShell 5.1 native-command argument passing.
- `allowed_hours` uses the clock of the machine running `lanq`. In tests the generated Linux scripts are
  checked with `sh -n` and the Windows scripts with the PowerShell parser, but only when those shells exist on the
  machine running the tests (otherwise those checks are skipped), and the scripts have so far been exercised only
  against fakes plus a local process-group test; report any real-host defects.

## Compared with interactive agent-fleet apps

Desktop apps such as [Orca](https://github.com/stablyai/orca) are interactive: you watch and steer many coding
agents, each in its own worktree, from a GUI, with diff review and PR integrations. LanQueue is a different shape:
a headless command that routes one job at a time to a machine by capability and load, with a policy file in front
of it. It does not run or supervise agents interactively, has no UI, and installs nothing on the remote machines.
They can complement each other; choose by whether you want a cockpit or a dispatcher. (Feature descriptions are
based on each project's public README at the time of writing; check the project for current details.)

## Contributing and licence

See [CONTRIBUTING.md](CONTRIBUTING.md). MIT licensed, see [LICENSE](LICENSE).
