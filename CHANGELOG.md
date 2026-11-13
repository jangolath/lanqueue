# Changelog

## 0.9.0 (unreleased)

### Per-job environment variables

- `lanq run --env NAME=VALUE` (repeatable; single runs, `--shards`, `--dry-run`) sets a variable for this job only.
  Shards get it too. The split is at the first `=`, and a name may be given only once (names are compared ignoring
  case).
- The developer decides which names jobs may set: machines.yml `policy.allowed_env`, exact names or prefixes ending in
  `*` (`[CONFIGURATION, TEST_FILTER, "MYAPP_*"]`). It is empty by default, so every `--env` is refused until the
  developer lists names. A name not listed is `Rejected` (2) with an `ask_developer` step.
- A hard deny table applies on top, even to listed names, because these variables can make an allowlisted command run
  other code or move where tools keep their files: the loader and shell startup names machines.yml already refused,
  `PATH`, Node (`NODE_OPTIONS`, `NODE_PATH`), .NET (`DOTNET_STARTUP_HOOKS`, `DOTNET_ROOT*`, `CORECLR_*`, `COR_*`, ...),
  Java, Python, Perl and Ruby startup and path variables, the Go toolchain and module settings and `CC`/`CXX`/`CGO_*`,
  `RUSTFMT` and `RUSTUP_TOOLCHAIN`, OpenSSL and glibc module paths, profile and system folders (`HOME`, `USERPROFILE`,
  `APPDATA`, `TEMP`, `TMP`, `XDG_CONFIG_HOME`, ...), `IFS`, `CDPATH`, and every name starting `GIT_`, `SSH_`,
  `NPM_CONFIG_`, `YARN_`, `MSBUILD`, `LD_`, `DYLD_` or `LANQ_`. `NODE_ENV`, `GOOS` and `GOARCH` stay allowed. An
  `allowed_env` entry the table fully covers (`GIT_*`, `HOME`) makes the config fail to load.
- Each value must be non-empty and free of control characters, at most 4,096 characters, and 16,384 for all of a job's
  variables together (`NAME=VALUE` each). Values are never echoed in a refusal.
- A machine whose own `env:` in machines.yml sets the same name is not used for the job (`env_conflict`), and neither
  is a Windows machine on which the values make the job's command longer than 8,000 characters
  (`windows_command_too_long`). Both are permanent. When every capable machine clashes, the run ends at once with
  `NoCapacity` (75, `retryable: false`) and an `ask_developer` step naming the variables and machines; when the
  length rules out the last ones, it is `Rejected` (2).
- `lanq.yml` gains `env:` in a job and in `defaults:`, a mapping of `NAME: value`. It is merged by name, the job's value
  winning: the one exception to "a job's value replaces the default". Each entry becomes an `--env` flag.
- Variables go after the machine's `env:` and before lanq's own `LANQ_*`, quoted as data like the machine's.

### Exclusive jobs

- `lanq run --exclusive` (lanq.yml `exclusive: true`) takes every slot of the machine (`max_jobs`), all or nothing, so
  no other lanq job, from you or anyone else, runs there while it does.
- It waits while any slot is in use (`busy`, retryable: "busy (slots 1/4; --exclusive needs all 4 free)"). There is no
  reservation, so on a machine that is never idle it waits out `--wait` and ends `NoCapacity` (75). Use `--on` with a
  machine that is idle, or run it in quiet hours.
- `--exclusive --shards N` puts one shard on each machine; `--shards auto` counts the idle machines. A fixed N above the
  machines that can take the job by config (capabilities, `size:`, `--env`) is refused (2) before any network.
  `--mem` and Windows arguments are not part of that count, so extra shards wait for a machine that is idle.
- In a plan, the dry run marks an exclusive job's machine full, so a sibling job waits.
- `lanq unlock`, the probe and the stale-slot reclaim are unchanged: each slot of an exclusive job is an ordinary slot
  file.

### Baseline rerun: is this failure new?

- `lanq run --baseline` (lanq.yml `baseline: true`): when a job, a shard or a plan job ends `Failed` (its own non-zero
  exit), lanq runs the same request again at the base commit, a clean tree, and says whether the failure is new.
  Never after `TimedOut`, `WindowClosed`, `InfraError`, `NoCapacity`, `Rejected` or `Cancelled`.
- The base is the merge-base of HEAD and the default branch (`origin/HEAD`, else `origin/main`, `origin/master`,
  `main`, `master`). `--baseline-ref REF` (lanq.yml `baseline_ref:`, implies `--baseline`) uses the merge-base of HEAD
  and REF instead. On the base commit with uncommitted changes, the base is HEAD without them; with none, the baseline
  is `skipped`. A REF that is not a commit, no default branch, or unrelated history is `Rejected` (2) before anything
  is sent. A shallow clone that lacks the merge-base is not refused: the baseline is `inconclusive`.
- The verdict is `new` (the base completed), `preexisting` (the base failed too, any exit code), `inconclusive`
  (anything else: timed out, no machine, the base could not be read or sent, Ctrl+C) or `skipped`. It is information
  only: the run's status and exit code never change.
- Shards: only the failed shards rerun, with the same `LANQ_SHARD`/`LANQ_SHARDS`. Plans: only the failed jobs that
  asked for it, after the whole plan has ended. Each rerun gets its own `--wait`.
- A rerun goes to the machine the job failed on: while that machine could take it but is busy, it waits up to 2 looks
  (about 15 s each) for a slot there, then takes any eligible machine. A held or ruled-out machine is not waited for.
  On another machine, `baseline.sameMachine` is `false` and the message and the `new` reason end `(on big-01, not
  build-01 where it failed: the result may reflect the machine)`. Waiting lines are prefixed `[baseline]`,
  `[baseline 2/4]` or `[baseline unit 2/4]`.
- Ctrl+C: a rerun not yet started is `inconclusive: cancelled (Ctrl+C) before it started` on every path (single,
  shards, plans); a running one is stopped and cleaned up (`Cancelled`, 130, inconclusive). A rerun that finds no
  machine is `NoCapacity` (75), inconclusive.
- `lanq.yml`: `baseline: false` with `baseline_ref:` on the same job is an error; an inherited `baseline_ref` is turned
  off quietly.
- A rerun of an older commit leaves the worker's newer stored base (and `bases.json`) alone, so the next normal job
  still gets a small incremental bundle.
- Output: a `[lanq] baseline: ...` line per rerun before the status line, a `baseline` object in `--json` (run, each
  shard, each plan job), and the verdict in the `fix_code` reason. `--dry-run` prints the base (`Baseline: on failure,
  rerun at 1a2b3c4 (merge-base with origin/main)`).
- The base commit's payload gets the same committed-name and package-config secret scans as any snapshot.
- A red run costs about twice as long with `--baseline`.

### JSON and records

- Run, shard and plan job objects (and the dry runs) gain `env` (the variable names only, never values),
  `exclusive: true` and `baseline` (`{ref, sha, status, exitCode, machine, jobId, logPath, durationSeconds, verdict,
  message}`, plus `sameMachine` when the rerun ran somewhere). Each is left out when it does not apply.
- New permanent reason codes `env_conflict` and `windows_command_too_long`.
- `jobs.jsonl` gains optional `envNames`, `exclusive`, and on baseline reruns `baselineOf` (the failed job's id) and
  `commit` (the base's short id); `v` stays 2. `lanq stats`, `--target-minutes` and the plan's history order leave
  baseline reruns out.
- Like the other job options, `--env`, `--exclusive`, `--baseline` and `--baseline-ref` are refused with `--plan`
  (set them in `lanq.yml`).

### Security

- `--env` is gated by `policy.allowed_env` (empty by default) and the hard deny table, and each `NAME=VALUE` goes
  through the secret scan (`Rejected` 2, value never echoed). A repository's `lanq.yml` can set only names the
  developer allowed. The table is a guard against the obvious ways a variable can turn an allowlisted command into
  other code; it is not a sandbox.
- Values are visible to other users of the worker (its process list) and appear in the dry run and in suggested
  commands in `nextSteps`, which repeat your command line. Never pass secrets with `--env`.
- Slot release now checks the holder: cleanup removes each slot (a normal job's single slot too) only while its first
  line is still this job's id, so a late cleanup can no longer delete a slot another job reclaimed meanwhile.
- A claim that did not report back (timed out, lost connection, unreadable output, Ctrl+C mid-claim) is followed by a
  best-effort release of the slots that hold this job's id (15 seconds, never fails the job).

### Fixes

- `--shards` with `--mem`: a shard that started while lanq was probing the machines was counted as running, as if its
  memory showed in the probe's answer, although the machine answered before it ran. The next round could then place
  one shard too many on that machine. Which shards are not running yet is now taken before the probe.
- Linux workers: stopping a stale job from inside the stale-slot reclaim or `lanq unlock` overwrote the loop's own
  variables, which could miss the reclaim, leave a `.reclaim` lock behind and report a slot as `REMOVED=` that was not.

### Tests

- Two tests that failed now and then on GitHub's Windows runner are now deterministic. The `--mem` placement test's
  fake machine now reports less free memory once a shard's upload is done, as a real job would use it. The newline-free flood test now measures the reader's
  memory on one thread, not process-wide, where work left by earlier tests counted too.
- The exclusive-claim race tests run the real sh scripts behind a ready/go barrier, including an exclusive claim that
  must back out while two normal claims race it.

### Dependencies

- The native binaries carry the .NET 10.0.15 runtime (was 10.0.14).

### Docs

- README sections for `--env`, `--exclusive` and `--baseline`; docs/PLANS.md, docs/JSON.md, docs/SHARDING.md,
  AGENTS.md, docs/AGENTS-GUIDE.md, the agent snippets, the skill and llms.txt cover them, including what an agent may
  and may not do with each. `machines.example.yml` shows a commented `policy.allowed_env`.

## 0.8.1 (2026-11-10)

### Security

- The .NET SDK search behind `lanq doctor --remote` and `lanq makeready --add` no longer proposes `path_prepend` for a
  folder other local users could plant code in, judged as lanq-makeready 0.3.0 does: only Administrators, SYSTEM,
  TrustedInstaller (root), members of the local Administrators group and the build account may write the folder, its
  `dotnet`, `sdk`, `host` and `shared` trees, and nobody else may rename or replace it through a parent folder (on Linux
  the file modes; POSIX ACLs are not read). On a shared PC, a world-writable folder such as `C:\tools\dotnet` could
  otherwise make every later .NET job run a planted program as the build account. Such a folder is skipped, and
  `doctor --remote` and `--add` say so. The search now goes to the worker on stdin and runs from memory, like
  `lanq makeready`, instead of on the command line.

### Behaviour changes

- `lanq makeready` runs lanq-makeready 0.3.0. On a Windows worker whose build account is denied WMI/CIM it now reports
  memory, CPUs, the Windows version and disks instead of 0 GB and "unreadable", and no longer reports OpenSSH Server as
  "installed but not running" when that account may not read the service status. Keys in a file `sshd_config` names
  (such as a `Match User` block's central file) are found. The .NET SDK search covers the same folders `lanq doctor
  --remote` searches, with the same ownership rule.
- The suggested `machines.yml` entry (text and `--add`) takes the script's `path_prepend` and `env: DOTNET_ROOT` when
  the newest .NET SDK is in a folder that is not the `dotnet` on PATH. lanq takes it only as exactly one absolute folder
  in printable ASCII with `DOTNET_ROOT` set to the same folder and none of `"` `\` `<` `>` `|` `?` `*` `;` `:`; anything
  else is left out. A folder the report hides (under the user's home, redacted) is shown as a comment, and `--add` then
  looks the SDK up itself as before. `--add` never writes a second `path_prepend:` or `env:` key or a second `dotnet`
  capability. `--json` passes the script's `suggestedMachine` through as reported.

## 0.8.0 (2026-11-10)

### Routing

- `--min-size light|medium|heavy` (that class or larger) and `--size C[,C]` (one of these classes) on `lanq run`
  (single, `--shards`, `--dry-run`) and `lanq health`. They do not go together. A machine's size is its `size:` in
  `machines.yml` when it has one, else the probed class (under 6 GB light; 16 GB or more with 4 cores heavy; else
  medium).
- Optional machine key `size: light|medium|heavy`: declare a 16 GB laptop `medium` to keep heavy jobs off it, or a
  machine whose memory cannot be probed. Without it nothing changes.
- New permanent reason codes `wrong_size` (the machine's size does not match) and `size_unknown` (no declared size and
  no reported memory). A declared size is judged from the config alone, so a job no machine can ever take for its
  size ends at once with 75 and `retryable: false`, before any ssh.
- `--on m[,m]` on `lanq run` (also `--shards`, `--dry-run`) and `lanq health`: only these machines, matched ignoring
  case; an unknown name is refused (exit 2) before any network. It only narrows: capabilities, size, `--mem`, holds,
  allowed hours, slots and wake rules all still apply. A pinned machine that is busy or held is `NoCapacity`
  (`retryable: true`, waits out `--wait`); one that can never take the job ends at once (`retryable: false`).
  Suggested next steps keep `--on`, `--size` and `--min-size` and never drop them.

### Plan files

- `lanq run --plan` runs the jobs of `lanq.yml` at the repository top (`--plan-file PATH` for another file):
  `version: 1`, optional `defaults:`, and up to 32 `jobs:`, each one `lanq run` line (`command`, `dir`, `needs`,
  `mem`, `size` or `min_size`, `on`, `timeout`, `shards`, `target_minutes`, `shard_retries`, `fail_fast`, `fetch`)
  plus `after:` to order them. Jobs run side by side on one snapshot of the repository and give one result.
- Every job is checked as its own `lanq run` line before anything is sent (allowlist, allowed directories, flags,
  command-line and payload secret scans, the Windows rule): one refusal refuses the plan (2), and a job no machine can
  ever take ends it with 75 before the snapshot.
- A job whose `after:` job did not complete is `Skipped`. Others keep running unless `--fail-fast` (running jobs are
  cancelled and cleaned up, the rest skipped). `--wait` counts only while no job of the plan holds a slot.
- `--only a,b` runs only those jobs; `--dry-run` prints each job's equivalent `lanq run` line and what it would do now
  (`WouldRun`, `WouldWait`, `Pending`, `NoCapacity`), sending nothing; `--json` prints one object with a `jobs` array.
  Exit 0 only when every job completed, else the first failed job's own code, then 2, 124, 70, 75, 130.
- `lanq.yml` has no keys for hosts, ssh, keys, env, paths, wake, priority or policy: unknown keys are refused with the
  line and a did-you-mean, and machine settings are pointed back to `machines.yml`. YAML anchors, aliases and merge
  keys are refused (share values with `defaults:`). A job can do nothing its `lanq run` line could not.
- Guide: https://github.com/jangolath/lanqueue/blob/main/docs/PLANS.md

### Windows workers

- `&` and `|` in arguments are allowed when the program is a native `.exe` (`dotnet test --filter "A|B"`): a bare name
  or a path with no extension or `.exe`, not `cmd` or a package-manager shim (`npm`, `npx`, `pnpm`, `yarn`,
  `corepack`). The worker checks again before starting anything: when the name resolves there (with `path_prepend:`)
  to anything but an `.exe`, nothing runs and the job is `Rejected` (2) naming what it found. `"` `<` `>` `^` `%` stay
  refused for every program.
- Workers whose build account is denied WMI/CIM now work: memory falls back to .NET (`GlobalMemoryStatusEx`), load to
  two `GetSystemTimes` samples, and with no source at all `lanq health` shows `load ?` and load never holds the
  machine. Each job runs in a Windows job object, so a timeout, Ctrl+C, a stale slot or cleanup ends the whole process
  tree without WMI (`taskkill` stays the fallback).
- `lanq doctor --remote` shows a `wmi` WARN for such a worker, with what the owner could grant.

### Setup

- `lanq init` without `--config` writes the per-user config file lanq reads without flags
  (`%APPDATA%\lanq\machines.yml`, `~/.config/lanq/machines.yml`), creating the folder; never the current folder.
- When the `dotnet` check fails (`dotnet --version` now needs an SDK: a runtime-only install counts as missing),
  `lanq doctor --remote` searches the worker's usual and private install folders (read-only, about five seconds) and
  names the `path_prepend:` and `DOTNET_ROOT` lines to add; `lanq makeready --add` proposes them in the entry it shows.
  lanq still never edits a config or a worker's `PATH` by itself.

### Behaviour changes

- `lanq health --json`: `size` is the effective size (the machine's `size:` when set, else the probe), and every row
  gains `sizeSource` (`probe` or `config`) and `loadKnown` (`false` when the worker could not measure its load). The
  text line changes only for a machine with `size:` (`(size: medium in machines.yml; probe: heavy)`).
- `jobs.jsonl` lines of a plan run gain optional `planJob` (the job's name) and `planRun` (shared by one plan run);
  `v` stays 2.
- Jobs of a plan run get `LANQ_PLAN_JOB=<name>` in their environment.
- On Windows, processes a job leaves running (build servers, for example) now end with the job.
- `lanq init` no longer writes `./machines.yml` (see Setup).

- Loading a config now checks the Windows job script at its largest (a job given `&` or `|`, the longest plan job
  name), so a Windows machine whose `env:` leaves no room for such a job is refused at load instead of at run time.
- With no per-user config folder (no usable `HOME`/`APPDATA`), `lanq init` and config lookup exit 2 and name
  `--config`/`LANQ_CONFIG`; a relative `XDG_CONFIG_HOME` is ignored.

### Security

- Windows workers run lanq's helper script (`work_root/bin`) only when its SHA-256 matches lanq's own copy, and lanq
  rewrites it before each job if it changed, so a job cannot plant code in later jobs' wrapper, stop, cleanup or doctor
  scripts, or stop a cleanup before it frees the slot.
- The job wrapper's `LANQ_EXIT` and `LANQ_WINARGS` lines carry a per-run random value that is not in the job's
  environment, so a job that prints them is ordinary output, not a refusal or an exit code.
- `lanq.yml` refuses YAML anchors and aliases (`&name`, `*name`) with their line number, so an alias-bomb file cannot
  hang lanq.

### Fixed

- Windows workers denied WMI showed `ram ?`, a silent `load 0.00` and size `unknown`, and a timeout, stop or stale-slot
  cleanup could leave the job's process tree running.
- `dotnet` passed `lanq doctor --remote` on a worker with only a .NET runtime, and the job then failed with "No .NET
  SDKs were found".
- With `--plan` and `--wait 0`, a job no longer ends `NoCapacity` when a sibling frees the slot just after the job saw
  it taken; it looks again first (also for jobs with shards).
- When several plan jobs fail, the plan exits with the first failed job's own exit code in `lanq.yml` order, whatever
  order they were launched in.
- When a Windows job times out, its process tree is walked before the job object is ended, so a grandchild started
  before the job object took hold is stopped too.

## 0.7.0 (2026-11-10)

### Agents and scripts

- `lanq doctor --json` (also with `--remote`): one object on stdout on every path, `{status, exitCode, message,
  remote, counts, checks, nextSteps}`. Each check is `{scope, machine, name, level, detail, fix, capability}`, so an
  agent can report the `fail` checks and their fix. Status `Ok` (0), `Warnings` (1), `Failing` (1), `Rejected` (2),
  `InfraError` (70) or `Cancelled` (130). The text lines go to stderr.
- `lanq stats --json`: one object with the totals, `logs`, `machines` (counts, median and p90 of completed jobs,
  `speedFactor`, `speedSamples`) and `commands` (runs, jobs, results, times, `lastShards` and `timing`: the work and
  per-shard overhead `--target-minutes` uses). At most 500 commands (`commandsMore` counts the rest). Times are UTC.
- `lanq run --dry-run --json` adds `shardCountWhy` and `shardTiming` (null when they do not apply); a `--shards`
  run with `--target-minutes` adds `shardTiming`.
- Agent docs: agents may run `lanq doctor --json`, `lanq doctor --remote --json` and `lanq stats --json`; on
  `InfraError` they run `lanq doctor --remote --json` and report the `fail` checks. Contract:
  https://github.com/jangolath/lanqueue/blob/main/docs/JSON.md

### Sharding

- `--shards auto --target-minutes M` (1 to 1440) picks N from earlier completed runs of the same command and `--dir`
  (the newest 10 of the last 90 days), so each shard takes about M minutes on the slowest machine it would go to,
  never more than the free slots. Without history it is plain `--shards auto`. The `--shards auto` line (and a
  `Timing:` line in `--dry-run`) says why. Plain `--shards auto` is unchanged. Timing history starts with 0.7.0 runs.
- Per-machine speed factors, learned from completed jobs in the job log (`x1.62` = 62% slower than typical; 5 jobs
  needed, else the median of machines of the same size, else 1).
- `lanq stats --target-minutes M` shows the shards M needs per command, without probing.
- Guide: https://github.com/jangolath/lanqueue/blob/main/docs/SHARDING.md (new: choosing N from history, and
  balancing shards by duration for pytest, Jest, Playwright, .NET and Go).

### Behaviour changes

- `jobs.jsonl` lines gain optional fields: `v`, `key`, `dir`, `shard`, `shards`, `runId` and `runSeconds` (the
  command's own run time). At 4 MiB the file moves to `jobs.1.jsonl`; lanq reads both, lanq 0.6 only the current file.
- `lanq stats` survives bad lines: a torn or unreadable line is skipped and counted, with one warning. It exits 70
  when the job log cannot be read.
- A locked `jobs.jsonl` no longer turns a finished job into `InfraError`: the append is retried, then skipped with a
  warning. Two lanq processes no longer collide on it.
- Doctor suggestions in `--json` output now carry `--json` (`lanq doctor --json`, `lanq doctor --remote --json`).
  Text output still suggests plain `lanq doctor`.
- Job logs in the state folder's `logs/` are pruned after a run, at most once a day: lanq's own logs older than
  `policy.keep_logs_days` (new; default 30, 0 = never) and the oldest beyond 5,000. A log modified in the last hour,
  other files and links are never deleted.
- `lanq doctor` prints one more line after `config`: `logs` with the count and size of the job logs (`WARN` over
  2 GB when `policy.keep_logs_days` is 0).
- `lanq stats` keeps its first lines, then adds a machine table (median, p90, last run, speed), the newest 20
  commands with a `timing:` line each, and a `logs:` line.

### Docs

- README: a Local state section (what `jobs.jsonl` and `logs/` hold, rotation and pruning; nothing leaves the
  machine): https://github.com/jangolath/lanqueue/blob/main/README.md#local-state

## 0.6.0 (2026-11-09)

### Agents and scripts

- `nextSteps` in every `--json` object: each run, dry run, `lanq health` row, init, wake, `makeready --all` and `--add`
  object and every error object. Each step is `{action, reason, command?, argv?, safeToRunUnattended}`. `action` is
  one of `proceed`, `retry`, `wait`, `fix_code`, `fix_command`, `ask_developer`, `run_doctor`, `run_health`, `none`
  (treat an unknown one as `ask_developer`). `argv` is authoritative; `command` is the same as one line for bash and
  PowerShell when that is safe. A suggestion never does more than the command it follows and is never `unlock`,
  `init`, `wake`, `--force`, `--yes` or `--add`. The single `lanq makeready <machine> --json` report is unchanged and
  has none.
- `retryable` and `excluded` on `NoCapacity` (exit 75): `retryable` is false when no machine can ever take the job as
  configured; `excluded` lists each machine with a `reason` code (`missing_capability`, `busy`, ...), `why` and
  `permanent`.
- `lanq run --dry-run`: what `lanq run` would do now (the same checks, snapshot scan, probe and choice: the machine,
  every excluded machine and why, `--shards` N and placement, the machine it would wake) without claiming a slot,
  sending, logging or waking anything. Exit 0 would start, 75 no machine now, 2 refused. `--json` prints one object
  with status `WouldRun`.
- `lanq health --needs a,b [--mem N]`: judges every machine for such a job as `lanq run` does
  (`-> eligible (2 slots)` or `-> not eligible: lacks gpu ...`); JSON rows add `eligible`, `why`, `reason`,
  `permanent` and `jobSlots`. Exit 0 when a machine is eligible, 75 when none is.
- `logPath` in every `lanq run --json` object (the job's local log; null when no job started) and `--log-tail N`
  (1 to 200, `--json` only) for its last N lines as `logTail`. Agents no longer need to redirect stderr to a file.
- A run cancelled with Ctrl+C after its job started now reports that job's `logPath`.

### Adding a machine

Every write is local; lanq still never changes a worker.

- `lanq makeready --ssh user@host --os windows|linux --setup` prints the PC's full one-time setup and connects to
  nothing: the build user, your public key in its `authorized_keys` (Windows: every grant by SID), the work folder
  (`C:/lq`, `/srv/lq`), firewall scoping, the host key fingerprint check, the first connection, then password logins
  off. Only `.pub` files are read.
- `lanq makeready --ssh user@host --os X --add --name NAME [--yes]` scans the PC and adds it to `machines.yml` only when
  it is READY: as text after the last machine, every other byte kept, checked before an atomic replace, never through
  a link. It asks first (`--yes` without a terminal and with `--json`), then probes the new machine once.
- `--key-env VAR` with `lanq makeready --ssh`: scan with that key and write `key_env: VAR` into the entry.
- `lanq makeready --ssh` that fails because the key was refused or the host key is unknown now suggests `--setup`.
- `lanq init`'s next steps now show `--setup`, then `--add --name`, then deleting `example-01`.

### Behaviour changes

- Fail fast on exit 75: when no machine can ever take the job (none has every `--needs` capability, `--mem` above
  every machine's RAM, git or tar missing, ...), `lanq run` ends at once with `retryable: false` instead of waiting
  out `--wait`, and never wakes a machine. The message starts `No eligible machine, and no machine can ever take this
  job as configured (waiting will not help).` The exit code is still 75.
- The `NoCapacity` message gives the reason waiting cannot cure first: a machine that is down and lacks a capability
  says `lacks gpu (...)` instead of its ssh error.
- `Next: ...` lines on stderr for people (`[lanq] Next: ...` in `lanq run`), before the `[lanq] log: <path>` line and
  the final `[lanq] <Status>: ...` line, which stays last. stdout is unchanged in text mode.
- `lanq health --json` rows gain `nextSteps`; nothing else in a plain `health` row changes.
- `lanq init`'s starter `machines.yml` uses `work_root: /srv/lq` (and `C:/lq` in its Windows example), the same
  folder `makeready` and `--setup` suggest.

### Releases

- Release notes from CHANGELOG.md now appear on nuget.org and on the GitHub release.

### Docs

- New: https://github.com/jangolath/lanqueue/blob/main/docs/JSON.md (the `--json` contract: every field, status,
  reason code and the `nextSteps` rules), a tool-agnostic https://github.com/jangolath/lanqueue/blob/main/AGENTS.md,
  https://github.com/jangolath/lanqueue/blob/main/llms.txt and
  https://github.com/jangolath/lanqueue/blob/main/docs/AGENTS-GUIDE.md with snippets for agent instruction files.
- Agent docs use `--log-tail` and `logPath`, a dry run before heavy work, `retryable`, and only `nextSteps` that are
  safe unattended.
- https://github.com/jangolath/lanqueue/blob/main/docs/TEAM-QUICKSTART.md follows `--setup` and `--add`, uses
  `C:\lq` and `/srv/lq`, grants by SID, cuts the work folder's inherited ACL, turns off keyboard-interactive logins on
  Linux and explains when a ufw LAN rule does nothing.

## 0.5.1 (2026-11-09)

### Getting a team's pool going

- **`lanq makeready` now runs lanq-makeready 0.2.0** (verified against its release SHA256SUMS and build-provenance
  attestation), which adds Wake-on-LAN and Fast Startup readiness, Windows 10 end-of-support, OpenSSH Server, firewall
  and default-shell checks, Linux end-of-life, sshd password and auto-suspend checks. lanq shows what 0.2.0 adds to the
  report: the script's `VERDICT:` line (READY / READY WITH WARNINGS / NOT READY) and its numbered next steps, blockers
  first, each with the command or settings path and whether it needs admin (or root), worded as the script prints them
  and sanitised; a `Suited for:` line; and, in the suggested `machines.yml` entry, `min_free_mem_gb` and (only when the
  script's Wake-on-LAN check passed) a commented `wake:` block whose redacted MAC you fill in. `--json` (and each report
  in `--all --json`) now carries every field as the script sent it, 0.2.0's `summary`, `suitedFor`, `min_free_mem_gb`
  and `wake` included, and no longer adds a `WorkerReady` field. A malformed `summary` or `suitedFor` makes the report
  unusable (exit 70). Reports from lanq-makeready 0.1 still work as before.
- **`lanq makeready --all`** runs the read-only lanq-makeready check on every machine in `machines.yml`, four at a
  time, with the same rules as `lanq makeready <machine>` (a `PAUSE` file or `allowed_hours` holds a machine unless
  `--force`; the script goes on ssh stdin), and prints one table: machine, OS, verdict (`OK`, `WARN n`, `FAIL n`,
  `unreachable`, `held`, or `error` for an unusable report) and the top problem, then points at
  `lanq makeready <machine>` for details. `FAIL` means NOT READY (no transfer mode works, so the single-machine
  check exits `1`); a `FAIL` check that does not block the worker (Docker, .NET) counts as a warning, as in the
  script's own verdict. The counts and top problem come from the report's summary (0.2.0), else from its checks. Exit
  `70` any machine unreachable or unusable, else `1` any NOT READY, else `75` any held, else `0` (warnings alone exit
  `0`, as for one machine); `2` bad arguments or config, `130` Ctrl+C. `--json` prints one object
  (`status`, `exitCode`, `machines`), each machine carrying the validated report exactly as the single-machine
  `--json` prints it.
- **`lanq init`** writes a commented, minimal starter `machines.yml` (current folder, or `--config FILE`) that loads
  as written: memory holds suited to low-RAM PCs, one example machine to replace, and commented Windows and Linux
  examples covering `key_env`, a .NET heap cap, `allowed_hours` and `wake:`. It never overwrites a file or writes
  through a link (an existing file, folder or link, even a dangling one, is refused with exit 2), prints the next
  steps (OpenSSH on the workers, connect once, `lanq makeready --ssh`, paste, `lanq doctor --remote`), and with
  `--json` prints one object (`status`, `exitCode`, `path`, `message`).

### Sharding across a mixed pool

- **`lanq run --shards N|auto -- CMD`** runs N jobs of one command at once (1 to 64), each its own lanq job with its
  own id, slot, log and cleanup. `{shard}` (1-based) and `{shards}` are replaced by numbers in the arguments only (no
  other expansion), and each job also gets `LANQ_SHARD` and `LANQ_SHARDS` next to `LANQ_JOB` (Linux env prefix and
  Windows wrapper). Every shard's substituted command line passes the allowlist, permission-flag ban, secret scan and
  Windows argument rule before anything is built or sent.
- **`--shards auto`** sets N to the free slots on the machines that can take the job now (capabilities, hours, `--mem`,
  holds), at least 1, at most 64, and prints the choice and why.
- **Placement:** shards are dealt round-robin over free slots, so a machine with `max_jobs: 4` runs up to four at once.
  Shards beyond the free slots wait for a sibling to finish; `--wait` counts only while no shard of the run holds a
  slot (an attempt whose slot claim fails never extends it). `--mem` is per shard: a machine takes at most
  floor(free memory / `--mem`) shards at once, also when `--shards auto` counts.
- **One payload:** the snapshot is taken and scanned once, and each bundle (full or incremental) or archive is built at
  most once for the whole run, one build at a time (a failed build is tried once more). The bundle carries the
  snapshot under a shared ref that each shard's prepare fetches into its own; lanq prepares (and cleans up) one job at a time per machine within a run, so shards on one worker do not race
  on its repository cache.
- **Results:** lines prefixed `[i/N machine]`, a summary table (shard, machine, status, exit, duration), exit 0 only if
  every shard passed (otherwise the failing shard's own code, then 2, 124, 70, 75; Ctrl+C 130). `--json` prints one
  object with `status`, `exitCode`, `shardCount`, `durationSeconds`, `message` and a `shards` array of run objects with
  `shard` and `attempts`. `--fetch` puts each shard's files in its own job-id folder.
- **`--fail-fast`** cancels the other shards after the first one that does not complete; **`--shard-retries R`** (0 to
  3) re-runs only a shard whose machine stopped answering ssh (never a test failure or a job that exited 255 itself),
  and never where it could run twice: if the connection dropped mid-run and the cleanup did not finish, the re-run goes
  to another machine, or does not happen when none can take it. An unexpected error ends only its own shard.
  Ctrl+C cancels every shard and still cleans each one up; Ctrl+C after every shard has finished keeps the real result.
- **`lanq health`** shows cores, total RAM, free/total slots and "suited for" (by lanq-makeready's rule and in its
  words, so a PC gets the same tier from both: the reported memory rounded up to whole GB, then light under 6 GB, medium
  6 to under 16 GB, heavy 16 GB or more with 4+ cores, "+ container builds" with a docker capability; a 16 GB PC that
  reports 15.8 GB is heavy; the Linux core count tries getconf, nproc, then /proc/cpuinfo and is left out (unknown)
  when none works); the probe now reports `mem_total_gb` and `cores` on
  Linux and Windows (unknown values are shown as `?`, never a hold). `health --json` adds `cores`, `memTotalGb`,
  `freeSlots`, `size` and `suitedFor`; earlier fields are unchanged. The text line now says `slots free F/M` instead
  of `slots U/M`.
- The Windows env cap checked at config load now includes the largest shard variables.

### Clearer first-run errors

- An ssh failure now says what to do, and still quotes ssh's own line: a host key not yet in `known_hosts` (connect
  once with `ssh user@host`, because lanq uses `StrictHostKeyChecking=yes`), a changed host key, `Permission denied
  (publickey)` (load the key, or put it in `authorized_keys`, or `administrators_authorized_keys` for a Windows
  administrator), a private key with loose permissions, an unknown host name, a refused connection, and a missing
  ssh client. Only when ssh itself failed (exit 255) and only on lines in ssh's own formats, so a remote command's output
  cannot produce one. Shown by `lanq health`, `lanq run`'s "no eligible machine", `lanq makeready` and `lanq doctor --remote`.
- `key_env` errors tell an unset variable apart from a path that does not exist, and say how to set it or use
  ssh-agent instead.
- A missing config points at `lanq init --config <path>`.
- "No eligible machine" says why each machine was passed over: the capability it lacks, its slots in use, or no free
  slot when the job tried to start, instead of "no match / full"; `lanq run --shards` says the same per machine.

### Docs

- **[docs/TEAM-QUICKSTART.md](docs/TEAM-QUICKSTART.md):** one orchestrator plus Windows 10/11 and Linux workers, from
  install to the first job: Windows OpenSSH Server, the `administrators_authorized_keys` trap, a non-admin build user,
  the firewall, sizing micro PCs, overnight family PCs, and the ten errors you are most likely to see.
- **[docs/SHARDING.md](docs/SHARDING.md):** per-ecosystem recipes (.NET helper scripts for sh and PowerShell, Jest,
  Vitest, Playwright, pytest-split, Go, cargo-nextest), what to keep local, sizing `max_jobs` and `min_free_mem_gb`,
  routing in a mixed pool, a worked 2 dev boxes + 4 micro PCs plan, and guidance for coding agents. The agent snippet and
  skill gained a "Shard long work across the pool" section.
- The MSBuild node-count environment variable is gone from `machines.example.yml`, the README, SHARDING.md and the
  starter config: it is not a documented MSBuild setting. Small PCs run `dotnet build -m:1` / `dotnet test -m:1` (one MSBuild process) instead.

### Dependencies

- Dependencies: GitHub Actions moved off the deprecated Node 20 runtime; test SDK 18.
- YamlDotNet 18.1.0 (config parsing unchanged; verified against 0.5.0 on 182 config shapes). A nested value where a plain one belongs (for example `env: {A: {b: c}}`) is now reported as a config error naming the line, not as a hint to edit C#.

## 0.5.0 (2026-11-09)

### Native binaries: `lanq` without the .NET SDK

- **Single-file native binaries** on every GitHub release, compiled ahead of time (NativeAOT, trimmed, symbols
  stripped, size-optimised): `lanq-win-x64.zip`, `lanq-win-arm64.zip`, `lanq-linux-x64.tar.gz`,
  `lanq-linux-arm64.tar.gz` and `lanq-osx-arm64.tar.gz`, each with `LICENSE` and `THIRD-PARTY-NOTICES.txt`. The Linux
  binaries are linked in Microsoft's .NET 10 cross-build image against its Ubuntu 18.04 sysroot, so they need glibc
  2.27 or later (the .NET 10 floor) and are smoke-tested inside Ubuntu 18.04 before a release is published; the
  arm64 Linux and Windows binaries are smoke-tested on arm64 runners.
- **One `SHA256SUMS`** covers every release asset, and every asset (binaries, `.nupkg`, `lanq.json`, `SHA256SUMS`)
  gets a build-provenance attestation (`gh attestation verify`).
- **Scoop:** each release attaches `lanq.json` (generated by `scripts/scoop-manifest.sh` from `SHA256SUMS`), so
  `scoop install https://github.com/jangolath/lanqueue/releases/latest/download/lanq.json` works and `scoop update lanq`
  follows new releases. No bucket repository.
- **`lanq --version`** (also `lanq version`) prints the version with its commit and the embedded lanq-makeready
  version, read back from the binary.
- **One code path for the tool and the binary.** The config is read through YamlDotNet's compile-time static context
  and the local state files (job log, bases) through source-generated JSON, in the NuGet tool as well; no
  reflection-based serialization is left, and the project is marked `IsAotCompatible`, so a trim or AOT warning fails
  the normal `-warnaserror` build. `allowed_hours` now has its own type (`HoursList`) because the static generator
  does not carry a property's `[YamlConverter]` attribute; a test loads every config key in every accepted shape.
  State files written by 0.4 read unchanged.
- **Config:** a duplicate key is now an error at every level instead of the last one silently winning: two `name:`
  lines in one machine, the same name twice under `env:`, or a second `allowed_commands:` under `policy:` (which used
  to replace the allowlist without a word). `os: 1` and `os: !!int 1` are refused (only the words `windows` and
  `linux`), and `max_jobs: !!float 2.5` is refused (0.4 truncated it to 2). Explicit standard YAML tags (`!!str`,
  `!!int`, `!!float`, `!!bool`, `!!null`, `!!seq`, `!!map`) still load as in 0.4 when they fit the value; `!!str` keeps
  a value a string (`allowed_hours: !!str ~` is an invalid entry, never "any time"). A tag that contradicts its value
  (`!!map` on a list, `!!int` on a word), the non-specific `!`, and any other tag (`!custom`, `!!binary`,
  `!!timestamp`) are refused by name and line, and so is an anchor that refers to itself (`capabilities: &s [*s]`,
  which used to crash lanq).
- **Release order:** nothing is published unless every binary built and passed its smoke test. The publish job puts
  all assets on a draft GitHub release, pushes the package to nuget.org, then makes the release public; a re-run after
  a failure reuses the draft only when it is empty or holds the same `SHA256SUMS` (`scripts/release-draft.sh`); a
  stale draft or a published release is refused. Runs for one tag are queued, never cancelled. A manual (dry) run
  builds and smoke-tests everything and publishes nothing.
- **Tags are validated:** the version must be strict semver (`scripts/release-version.sh`, no build metadata) and is
  passed to every step through `env:`, never pasted into a shell script. A tag with a pre-release part
  (`v0.5.0-rc.1`) becomes a GitHub prerelease that is never marked latest, so the Scoop `latest` URL never serves a
  release candidate. Workflow checkouts no longer keep the token (`persist-credentials: false`).
- **Friendlier config errors.** A key left without a value (`max_jobs: ~`, a bare `os:`, `machines: ~`, `policy: ~`,
  `env: ~`), an empty list entry (`allowed_commands: [x, ~]`, `""`), a file that is not a mapping, or a duplicate key
  is now a config error that names the key and line, instead of a crash or a cryptic message; `--json` still prints its
  one object. `os:` accepts only the words `windows` or `linux` (a number such as `os: 1` was read as an enum value
  before). Only `allowed_hours`, `key_env` and a machine's `min_free_mem_gb` may be left empty, meaning "not set". A
  YAML alias (`allowed_hours: *night`) works.
- **CI** publishes and smoke-tests a linux-x64 native binary on every pull request, so AOT breakage shows up there.
- The .NET runtime patch compiled into the binaries, and the trim/AOT analyzer package, are pinned
  (`LanqRuntimePackVersion`, 10.0.14) so builds and lock files do not move with the SDK patch; the native publish
  restores in locked mode against its own `packages.native.lock.json`. `scripts/check-runtime-pin.sh` fails the release
  and the CI AOT job when the SDK carries a newer 10.0 runtime than the pin, and a weekly `runtime-pin` workflow fails
  when Microsoft has shipped one, so a .NET security patch cannot be left out of the binaries unnoticed.

### Fetch: bring job outputs back

- **`lanq run --fetch <pattern>`** (repeatable) brings files the job produced back from the worker's job folder after
  the job ends (completed, failed, timed out or stopped by its window) and before cleanup, into
  `.lanq/artifacts/<job id>/` in the repository (`--fetch-dir DIR` for another place; the default `.lanq` folder gets a
  `.gitignore`). Patterns are relative to `--dir`, with `*`, `?` and `**` only; absolute paths, drive letters, `..`,
  backslashes, `:`, control characters and a leading `-` are refused. Nothing is fetched when the job never started.
- The worker is treated as untrusted: only regular files whose real path is inside the job folder are sent; symlinks,
  junctions and reparse points (never followed), hard links (Linux), devices, FIFOs and names with control characters
  are skipped and listed. Files come back as a framed text stream (header, base64 lines, SHA-256), never as an archive;
  lanq re-checks every path (including Windows device names, also as `NUL .txt`, trailing dots and spaces, length and
  case-insensitive collisions; matched in linear time), the caps, the exact size and the hash. Each file streams to
  a short `.lanq-partial-N` file in the same folder (CreateNew) and is renamed only when complete, never over an existing file; a failed or
  interrupted file is deleted. Nothing is written through a symlink or junction from the repository top (or
  `--fetch-dir`) down, so a committed `.lanq` link is refused. Only stdout carries files; the worker's stderr is a
  separate, sanitised diagnostic.
- A job that timed out or was stopped by its window is stopped on the worker (recorded process group on Linux, pid
  and start time on Windows) before its files are fetched.
- The Linux job wrapper now records the group leader's start time (field 22 of `/proc/PID/stat`) and the boot id
  (`/proc/sys/kernel/random/boot_id`) next to its process group, and every job runs with `LANQ_JOB=<job id>` in its
  environment (Windows jobs get it too; `env:` names starting with `LANQ_` are now refused as reserved). Cleanup, that stop and stale-slot reclaim share one kill. It acts only in the
  boot the pid file was written in, and then signals the group only when the leader still has that start time, or the
  leader is gone and a live member of the group carries this job's `LANQ_JOB`. So leftover job processes are killed,
  and a pid or group number reused by another program (after a reboot, say) is left alone. A pid file without a start
  time, or a worker without /proc, falls back to the leader's cwd inside the job folder, or `ps -o pgid=`.
- Caps: 500 files and 200 MB by default, `--fetch-max-files N` (up to 5000) and `--fetch-max SIZE` (up to 2G); what
  fits is fetched in sorted order and the rest is listed.
- The fetch has its own 10-minute limit and never runs more than 2 minutes past a machine's allowed hours plus grace.
  The slot lease allowance grows from 40 to 50 minutes to cover it.
- Fetch problems never change the exit code. `lanq run --json` gains `fetched` (`path`, `bytes`, `sha256`) and
  `fetchSkipped` (`path`, `reason`), present on every path when `--fetch` is given, plus `fetchDir`, `fetchError` and
  `fetchSkippedMore`; without `--fetch` the object is unchanged. That includes the early exits: a refused command line
  (a bad `--wait` value or `--fetch` pattern) and a missing or invalid config.
- A job whose ssh connection drops mid-run is stopped and its files fetched on a fresh connection, before cleanup (which
  would kill it anyway); if the machine does not answer that either, `fetchError` says "not fetched: the SSH connection
  failed during the job". A job that itself exits 255 is not mistaken for this: the job wrapper ends its output with a
  `LANQ_EXIT=<job id>:<code>` line (taken off the output), and ssh's 255 is the job's own exit only when that line is
  the final one and says 255; it decides nothing else.
- `lanq doctor --remote` on Linux: a tool check that ignores SIGTERM and is killed by `timeout -k` (exit 137 after the
  full check limit) is reported as `timeout`, not as exit 137, and probing for a working `timeout` (here and in the job
  wrapper) no longer gives up when merely starting `true` takes over a second on a loaded worker.
- Windows workers receive the fetch script on ssh stdin (as base64 of its UTF-16 text), so patterns do not count
  toward the 8000-character command line.

### Wake-on-LAN for sleeping workers

- **Wake-on-LAN for sleeping workers.** An optional per-machine `wake:` setting (`mac` required as `aa:bb:cc:dd:ee:ff`
  or `aa-bb-cc-dd-ee-ff`; `broadcast` an IPv4 literal, `255.255.255.255` (default) or a private or link-local address, anything else is refused; `port` 7 or 9, default 9;
  `wait_seconds` 1 to 600, default 120). `lanq run` sends one magic packet only as a last resort: when no awake machine can take the
  job, `--wait` is above 0 and `--no-wake` is not given, to the first-by-name machine that looks asleep (ssh's own
  connect timeout or unreachable-host error; never refused, auth or host-key failures), has the capabilities the job
  needs and is inside its `allowed_hours` on this computer's clock with at least `policy.min_window_minutes` plus its
  `wake.wait_seconds` of the window left (otherwise it would only be held as "window closing" once awake; the reason is
  shown instead). The wake happens inside `--wait`, at most once per
  machine per invocation. `lanq health` stays read-only; `lanq health --wake` wakes sleepers (same rule) and waits for them, probing
  with backoff until they answer or `wait_seconds` run out. A localized ssh message disables the automatic wake (fails
  safe).
- **`lanq wake <machine> [--wait] [--json]`** sends the packet even outside `allowed_hours` (you asked by name) and says
  so; `--wait` waits for ssh. `--json` prints exactly one object on every path.
- `lanq doctor` reports the wake setting and
  reminds that Wake-on-LAN must be enabled in the BIOS/UEFI and the network card driver and usually needs wired Ethernet.
- `wake:` is read through the same static config context as every other key, so it loads in the NuGet tool and the
  native binaries alike, with the same rules: an unknown or duplicate key inside `wake:` is an error, standard YAML tags
  load as the untagged value, and `wake: ~`, a bare `wake:` or a `wake:` key without a value is an error naming the key
  (leave `wake:` out for no Wake-on-LAN). A test fails when a config class is missing from the static context.
- Docs: Windows and Linux Wake-on-LAN setup in `docs/WORKERS.md`; the magic packet is unauthenticated and LAN-only,
  which is why it is opt-in per machine (SECURITY.md). The agent snippet and skill recommend `--wait 120 --no-wake`
  unless the developer has allowed waking machines, since any `--wait` above 0 can wake one.

## 0.4.0 (2026-11-09)

Workers and windows: making old and shared PCs safe to lend to the queue.

- **Memory in health.** The probe reports available memory (Linux `MemAvailable` and free swap, plus PSI memory
  pressure `some avg10` where `/proc/pressure/memory` exists; Windows free physical memory) and `lanq health` shows
  `mem free X.X GB`. A machine with less than `policy.min_free_mem_gb` (default 1.0, per-machine `min_free_mem_gb`)
  is held ("low memory (x.x GB free)"), and on Linux one whose pressure is above `policy.max_mem_pressure` (default
  20) is held ("memory pressure"). A machine that does not report memory is not held for it.
- **`lanq run --mem <GB>`** sends a job only to a machine reporting at least that much available memory (never to one
  that cannot report it).
- **Per-machine `env:`** sets variables for the job process only, for example heap caps on small machines
  (`DOTNET_GCHeapHardLimitPercent`, `NODE_OPTIONS=--max-old-space-size=2048`, `MSBUILDNODECOUNT`,
  `CARGO_BUILD_JOBS`). Names must match `^[A-Za-z_][A-Za-z0-9_]*$`, values may not be empty (Windows PowerShell 5.1
  cannot set an empty variable, so both platforms refuse one) or contain control characters, and, as a best-effort
  guard, loader or shell hijack names (`LD_*`, `DYLD_*`, `GLIBC_TUNABLES`, `BASH_ENV`, `ENV`, `SHELLOPTS`, `BASHOPTS`,
  `PS4`, `PROMPT_COMMAND`, `PSModulePath`, `COMSPEC`, `PATHEXT`) are refused. Values are passed as quoted data in both
  shells.
- On a Windows machine, `env:` and `path_prepend:` count toward the 8000-character encoded command line: a config whose
  values alone would pass it in the job script (with no job arguments) is refused at load, naming the machine and how
  much is over; `lanq doctor --remote` checks its own script and reports an overflow as a FAIL line for that machine; a job whose arguments pass it says the limit and the actual size.
- **Per-machine `path_prepend:`** puts absolute directories in front of `PATH` for the job, so tools installed by nvm,
  pyenv, rustup or sdkman are found in a non-login SSH session. `PATH` itself cannot be set through `env:`.
- **`priority: low|normal`** (default `low`): Linux jobs run under `nice -n 10` and `ionice -c3` when available;
  Windows jobs run at BelowNormal priority, inherited by their child processes. **This changes the default for
  existing configs**; set `priority: normal` on a dedicated build box.
- **PAUSE file.** If `<work_root>/PAUSE` exists on a worker, the machine is held ("paused on the machine"). Anyone at
  that PC can create or delete it. On Linux `PAUSE` or `pause` counts (those two spellings), on Windows any case. A job
  that is already running is not affected.
- **The worker enforces the job limit itself.** A job's own limit (`--timeout`, or the window end plus grace) plus one
  minute is enforced on the worker too, so a job outlives a vanished `lanq` by at most that minute: on Linux the job runs
  under `timeout -k 30 <seconds>` when `timeout` works there (exit `124`, children included); on Windows it runs in a
  child PowerShell that is killed with its whole tree (`taskkill /T /F`) when the wait runs out (exit `124`). The
  Windows wrapper records both its own and the child's pid with their start times in `locks/job-<id>.pid`, so cleanup
  and a stale-slot reclaim also reach a child whose wrapper died, and kill only processes whose start time matches. The Linux
  job wrapper now runs `/bin/sh` by absolute path, so a `path_prepend:` folder cannot supply its own `sh`.
- **Availability windows v2.** `allowed_hours` still accepts one string (a bare `allowed_hours:` or `~` still means any
  time), and now also a list of entries
  `[Days ]HH:mm-HH:mm` with days `Mon`..`Sun`, ranges (`Mon-Fri`) and lists (`Sat,Sun`). A window that crosses
  midnight belongs to its start day. The start minute is inside a window and the end minute is not (0.3 also counted
  the end minute). Errors name the bad entry.
- **Windows use the worker's clock.** The probe reports the worker's local time; output from an older probe falls back
  to the clock of the machine running `lanq`, and `lanq health` then says `time: local`.
- **Daylight saving.** The probe also reports `now_epoch=`, `tz_start=` and `tz_offsets=`: the worker's UTC offset in
  minutes on every UTC quarter hour from the one at or before now for the next 36 hours (PowerShell
  `TimeZoneInfo.Local`; on Linux `date -f -` in one call with GNU date, else `date -d @EPOCH +%z` per value, which
  busybox accepts). Clock changes happen on a UTC quarter hour, so the table places them exactly. The end of a window
  is turned into a real instant with that table, so a window over a clock change counts real time: a local end time
  that happens twice (clocks go back) is its earlier instant, and one that does not exist (clocks go forward) is the
  instant of the jump. A worker whose `date` cannot do this (or an older probe) gets wall-clock time, which can be an
  hour off on such a night.
- **Window close.** On a machine with windows a job may run until the window ends plus `policy.window_grace_minutes`
  (default 15) or until `--timeout`, whichever is sooner. If the window limit stops it, the job is cleaned up like a
  timeout, exits `124`, and the status line is `[lanq] WindowClosed`. A machine whose window ends in less than
  `policy.min_window_minutes` (default 10) takes no new job ("window closing"). The same check runs again after upload
  and prepare: if what is left of the window plus grace is then below `policy.min_window_minutes`, the job is not
  started, the machine is cleaned up, and the result is `NoCapacity` (exit `75`, "window closing").

Transfer and supply chain:

- **One cache per repository on git workers.** The single shared `<work_root>/repo.git` is replaced by
  `<work_root>/repos/<repoid>.git`, where `<repoid>` is the first 16 hex digits of the repository's root commit (the
  ordinally smallest one if the history has several roots). Different projects no longer share one object store. An
  existing `repo.git` is left alone and no longer used; delete it whenever convenient.
- **Hardened git on workers.** Every git call in the worker scripts (init, fetch, worktree add/remove/prune,
  update-ref) runs with `-c core.hooksPath=<null device> -c core.fsmonitor=false -c protocol.allow=never
  -c protocol.file.allow=never` (`/dev/null` on Linux, `NUL` on Windows); only the bundle fetch re-allows reading
  that one local file. A hook planted in a worker cache, or a hooks path in the worker's git config, no longer runs.
  Every git call also runs with `-c core.attributesFile=<null device>`, `GIT_CONFIG_NOSYSTEM=1` and `GIT_CONFIG_GLOBAL`
  at the null device (git 2.32 or later; `lanq doctor --remote` warns "git older than 2.32: global config not
  isolated" for an older worker git, which still reads the global config). Before each fetch, prepare deletes the
  cache's `info/attributes` and checks the cache's own `config`: any key besides the ones `git init --bare` writes
  (`core.repositoryformatversion`, `filemode`, `bare`, `symlinks`, `ignorecase`, `precomposeunicode`,
  `logallrefupdates`, `extensions.objectformat`, `refstorage`), for example `filter.*`, `include.*`, `includeIf.*`,
  `alias.*`, `remote.*`, `credential.*` or `url.*`, or any top-level file git does not create there (above all a
  `commondir` or `gitdir` file, which git follows even in a bare repository; a `reftable/` ref store is fine), makes it delete and recreate that cache (one line in the job output;
  that job sends a full bundle). Tests plant a smudge filter, an `include.path`, a `commondir` to another repository
  and a global config filter and check that none of them runs.
- **Incremental bundles.** `lanq` records, per machine and repository, the last commit a worker has fetched
  (`<state>/bases.json`, written atomically, updated only after a successful fetch), and the worker cache keeps
  `refs/lanq/base` on the last fetched snapshot. The next job sends `git bundle create … --not <base>` when that commit
  is still a local ancestor of the snapshot, so usually only the new commits and the uncommitted overlay travel. The
  worker checks the bundle with `git bundle verify` before fetching; if its cache lost the base, the job falls back
  once to a full bundle automatically (one log line says so). Rewritten history, a pruned base or a missing or
  unreadable `bases.json` simply mean a full bundle. A `bases.json` that cannot be read or written (for example locked by a
  parallel `lanq` on Windows) is retried twice, then one warning line is printed and the job goes on. Bundles are now built when a git machine actually gets the job.
  Archive mode is unchanged.
- **Secret scan of committed file names.** Besides the uncommitted overlay, the names of every committed file in the
  HEAD tree are checked against the same list as uncommitted files (one shared definition): `.env`, `.env.*`, `.envrc`, `id_rsa`, `id_dsa`, `id_ecdsa`, `id_ed25519`, `secrets.json`, `credentials.json`,
  `.git-credentials`, `.netrc`, `_netrc`, `terraform.tfstate` (and `.backup`), `*.pem`, `*.pfx`, `*.p12`, `*.key`,
  `*.ppk`, `*.kdbx`, `*.tfvars`, `*.jks`, `*.keystore`
  (`*.local.yml` / `*.local.yaml` only when uncommitted), because a bundle or archive would carry them. A match refuses
  the job before anything is sent. `.npmrc`, `.pypirc` and `.yarnrc.yml` files, committed or uncommitted, are not
  refused by name (registry-only ones are common): their content (up to 64 KB; lines end at CR, LF or CRLF) is checked,
  and only a literal credential refuses the job (`.npmrc`: a non-empty `_authToken`, `_auth`, `_password` or
  `password`, including `//host/:_authToken=`, unless it is just an environment reference such as `${NPM_TOKEN}`;
  `.yarnrc.yml`: a non-empty `npmAuthToken` or `npmAuthIdent`, also under `npmScopes`, with the same environment
  reference rule (a YAML comment after the value is ignored; a key left empty whose value continues on a more-indented
  next line, a block scalar `|` / `>`, or any key containing a backslash, since a double-quoted key decodes escapes
  such as `"npm\x41uthToken"`, is refused), and any other line naming one of those keys, such as
  YAML flow style `{acme: {npmAuthToken: x}}`, which is refused even with a reference (the message says to use a
  block mapping); an environment reference is exactly `${NAME}` or npm's optional `${NAME?}`, so
  `${NPM_TOKEN:-literal}` counts as a literal; `.pypirc`: a non-empty `password`). The refusal names the file and key, never
  the value. Obvious samples pass: names ending in `.example` or `.sample`, names containing
  `.example.`, and anything under a `fixtures/` or `testdata/` folder. **Behaviour change:** a repository that commits
  such a file (for example a test certificate outside those folders, or an `.npmrc` with a literal token) is now refused until the file is
  renamed or moved. A long list of findings is cut to the first ten.
- **Stale slots are reclaimed.** A slot file now records its own deadline (worker clock: now + the job's `--timeout`
  + 40 minutes for transfer and cleanup). When a job looks for a slot, a slot more than 30 minutes past its deadline
  (left by a crashed or killed `lanq`) is reclaimed: under an exclusive lock file `slot-N.reclaim` (one older than ten
  minutes, left by a reclaimer that died, is renamed away and checked again, and put back if it turns out to be a
  live reclaimer's fresh lock; each lock holds its creator's unique token, and a reclaimer removes only a lock that
  still holds its own) the slot is read again, and only if it is still that stale file
  is it renamed away to a unique name, deleted and created afresh like a free slot (if another job wins it, the next
  slot is tried; nothing is ever put back, and no hard links are needed). If the stale slot's job is somehow still
  running (its `locks/job-<id>.pid` names a live process: on Linux one whose working folder is in that job's folder,
  on Windows a process whose start time also matches the one recorded with its pid), its process group or tree is
  killed first; a pid Windows has since given to another process is never touched. A live slot, or one without a valid
  deadline (written by an older lanq, or not 1 to 18 decimal digits; `08` is read as 8), is never touched.
- **Supply chain.** Every project commits a `packages.lock.json` and CI restores in locked mode
  (`RestoreLockedMode` when `CI=true`); Dependabot proposes grouped weekly NuGet and GitHub Actions updates; the
  release workflow attests the `.nupkg`'s build provenance (`actions/attest-build-provenance`, pinned by SHA, with
  `id-token`/`attestations` write granted to that one job). README: "Verify a release" with `gh attestation verify`.

Command line:

- **`lanq makeready <machine>`** and **`lanq makeready --ssh user@host --os windows|linux`** (for a PC not in
  `machines.yml` yet) run the read-only [lanq-makeready](https://github.com/jangolath/lanq-makeready) v0.1.0 check on a
  worker. The two scripts are built in, byte-identical to that release (a test pins their SHA-256 from its
  `SHA256SUMS`; `scripts/sync-makeready.sh <tag>` updates them only after `sha256sum -c` and `gh attestation verify`),
  sent on ssh stdin with `-Redact`/`--redact` and run from memory: nothing is written to the worker. The JSON report
  is treated as untrusted (1 MB cap, source-generated parsing, schema `lanq-makeready/v1`, every string sanitised,
  nothing executed). Output: verdicts, each WARN/FAIL check with its advice, the capability diff against
  `machines.yml`, a suggested entry, and "lanq never changes a worker". `--json` prints the validated report. Exit `0`
  ready, `1` not ready, `2` bad arguments, `70` ssh or unusable report, `75` the machine is paused or outside its
  allowed hours (`--force` scans anyway).
- **`lanq doctor`** checks your own setup with no network: ssh/scp/git/ssh-keygen on PATH, the state folder, the
  config, and per machine a `known_hosts` entry (resolved with `ssh -G`: `[host]:port` for other ports, a
  `HostKeyAlias` as it is, and both the user and the global known_hosts files) and the `key_env` file and passphrase.
  **`lanq doctor --remote`** first probes each machine and, like `makeready`, leaves one that is paused or outside its
  `allowed_hours` alone (a `HOLD` line; `--force` checks it anyway). Then it makes one ssh call per machine (four at
  a time): `work_root` writable, git or tar (and, for git, a WARN when the worker's git is older than 2.32), and each
  capability's tool (`dotnet`, `node`, `npm`, `python`, `go` with `GOTOOLCHAIN=local`, `rust` with
  `RUSTUP_AUTO_INSTALL=0`, `java`, `docker`) run in the job environment, so a tool missing from a non-login SSH session
  (fix: `path_prepend:`) shows up before a job fails. Each check may take 20 seconds (Linux `timeout`, Windows a child
  PowerShell killed with its tree); a hanging one is reported as timed out and the others still run, and if the whole
  call runs out of time what did arrive is still reported. Exit `0` when no line is WARN or FAIL.
- **`--json`** for `lanq health` (one array: `name`, `state`, `reasons`, `load`, `diskFreeGb`, `memFreeGb`,
  `slotsUsed`, `maxJobs`, `localTime`) and `lanq run` (the job's output goes to stderr, then one object on stdout:
  `status`, `exitCode`, `machine`, `jobId`, `durationSeconds`, `message`). Agents should prefer it. `run --json` prints
  exactly one object even when no job ran: a bad argument or config (`Rejected`, exit `2`, `jobId` null), Ctrl+C
  (`Cancelled`, `130`) or an unexpected error (`InfraError`, `70`, also for a cancellation nobody asked for); `health
  --json` prints `{"status":"Rejected","exitCode":2,"message":...}` for a bad argument or a config that cannot be read,
  and a `Cancelled` (130) or `InfraError` (70) object instead of its array when interrupted or broken. Usage text after an unknown command
  goes to stderr (exit `2`); `lanq --help` still prints it on stdout. `makeready --json` likewise prints exactly one
  object: the validated report, or `{"status","exitCode","message"}` with `Rejected` (2), `InfraError` (70), `Held` (75)
  or `Cancelled` (130). (`lanq doctor` has no `--json`.)
- **`lanq unlock <machine>`** removes slot files past their recorded deadline at once (the same locked reclaim, which
  also stops such a slot's job if it is somehow still running) and says what it removed and kept; `--all --yes` removes every slot file (also pre-0.4 ones without a deadline), after a warning that a running
  job there would lose its slot. `--all` without `--yes` is refused.
- Options are checked per command: an option the command does not use (for example `lanq health --needs x`) is now an
  error (exit `2`) instead of being ignored, and only `lanq run` takes a command after `--` (anything else with `--` is
  exit `2`).
- Input checks: every pattern that validates user input or a worker's report (machine name, `ssh`, `work_root`, the
  values of a makeready suggestion) must match to the very end (`\z`), so a value with a trailing newline is refused.
  Error messages that quote what you typed (config errors, unknown options) are sanitised like worker output, and
  that sanitising now also replaces U+061C, U+2028, U+2029 and U+2060 to U+2064.
- `scripts/sync-makeready.sh` requires exactly one `SHA256SUMS` line for each of the two scripts (two lines for one
  script no longer pass), and exits non-zero after cleaning up when interrupted (INT 130, TERM 143).
- Fixes: an uncommitted `.npmrc` / `.pypirc` gets the same content check as a committed one (it was refused by
  name); the health probe no longer counts the transient `slot-N.stale.<pid>` file of a slot reclaim as a used slot.

## 0.3.1 (2026-11-09)

- **Security: PowerShell quoting.** Windows arguments are single-quoted for PowerShell, which also treats the
  typographic quotes U+201A and U+201B as single quotes. They were not escaped, so an argument containing one could
  close the string and run code on a Windows worker. All five single-quote characters are now doubled, checked by a
  round trip through a real PowerShell in the tests.
- **Security: cmd.exe metacharacters on Windows.** PowerShell 5.1 passes embedded double quotes to native programs
  unescaped, and `.cmd` shims such as `npm.cmd` re-parse the command line through `cmd.exe`, so `npm test "&calc"`
  could start a second program. A job whose arguments contain `"` `&` `|` `<` `>` `^` `%` now skips Windows workers
  and runs on a Linux worker; it is refused (exit 2, before any network work) only when no Linux worker has the
  needed capabilities. Linux workers are unchanged.
- **Security: untrusted output.** Control characters (C0 except tab, DEL, C1), bidi controls and zero-width
  characters from a worker are now shown as `?` on the console and in the job log, so a job cannot move the cursor,
  clear the screen, retitle the window or disguise text. The same applies to the error line `lanq health` prints.
- **ssh hardening.** ssh and scp always pass `ForwardAgent=no`, `ClearAllForwardings=yes`, `PermitLocalCommand=no`,
  `ForwardX11=no`, `ForwardX11Trusted=no`, `Tunnel=no` and `RequestTTY=no`, whatever `~/.ssh/config` says.
- **Timeouts.** A job that runs past its limit now ends with exit code `124` and "timed out after N min" instead of
  being reported as an SSH failure (`70`). New `lanq run --timeout <minutes>` (default 240). A job's own exit 124 is
  told apart by the `[lanq] TimedOut` status line.
- **Bounded memory.** Worker output is read in small chunks (LF, CR and CRLF each end a line, as before) and a single line is capped at 16 KB (cut with a
  marker), so even a newline-free flood costs a fixed amount of memory. A long job keeps only the last 64 KB for its
  result (the full output still streams to the console and the log); the health probe and slot claim keep only the
  first 64 KB. No output line is delivered after a command has returned, so a timed-out job cannot crash `lanq`.
- **Faster health checks.** `lanq health` and `lanq run` probe all machines at once instead of one after another;
  output order is unchanged.
- `allowed_hours` with the same start and end (for example `"22:00-22:00"`) is now a config error instead of a
  window that matches a single minute.

## 0.3.0

- **Archive transfer mode.** A machine can set `transfer: archive` in `machines.yml` (default stays `git`, the
  recommended mode). The same snapshot (HEAD plus uncommitted and untracked files, honouring `.gitignore`, never
  `.lanq/`) is sent as a deterministic `tar.gz` and extracted by the worker's `tar` into a fresh per-job folder, so a
  worker no longer needs git. Guarantees: `lanq` never builds an archive with an absolute, `..`, backslash or
  Windows-unstorable path, and skips (and reports) symlinks and submodules; the worker accepts only GNU tar or bsdtar (the
  Windows built-in one on Windows) (busybox tar workers are refused, gzip is required on Linux), lists the archive first and
  refuses it, writing nothing, if any entry is absolute, has `..` or a backslash, is not a plain file or directory,
  or is listed as a link (hard links and symlinks). Windows workers call `System32\tar.exe` by full path, the health probe checks once that it accepts the restricting
  flags (`--no-acls --no-xattrs --no-fflags --no-same-owner --no-same-permissions`), the extract runs exactly once
  with them (no fallback, stderr kept in the job log), and cleanup
  verifies the job folder is really gone. The secret scan, allowlists, directory checks and process-group kill are
  unchanged. Opt-in tests (`LANQ_TAR_TESTS=1`) run the generated scripts against hostile archives.
- The archive is built only when an archive machine actually gets the job, so git-only fleets never build one.
- `lanq health` checks `git --version` on git machines and a supported `tar` on archive machines, and holds a machine
  that lacks its tool, with the reason.
- `transfer:` now accepts only the words `git` or `archive`.
- NuGet-specific package readme and published install instructions.
- Old PCs pitch: a worker needs only a LAN connection, SSH, tar (or git) and your toolchain.

## 0.2.0

- Documentation: keeping SSH keys safe, how it works, using lanq with coding agents.
- Safer defaults from pre-publish review: secret scan hardening (bounded patterns, passphrase and URL-credential
  rules), per-user state location outside the repository, job process-group kill on cleanup, stricter health
  parsing.
- Release to NuGet via Trusted Publishing, and a worker setup guide.

## 0.1.0

- First release: headless, capability-routed job runner for LAN machines over key-only SSH.
