# Plan files: lanq.yml

A plan file lists the jobs a change needs (build, unit tests, architecture tests, integration tests, a serial tail) so
one command runs them all across the pool, side by side, and gives one result:

```bash
lanq run --plan
```

Each job is exactly one `lanq run` line. A plan adds no power beyond those lines: the allowlist, allowed directories,
banned flags, the secret scans and the Windows rule apply to every job, and nothing runs on your own PC.

## Where it lives

`lanq run --plan` reads `lanq.yml` at the repository top: the nearest folder up from the current one that holds `.git`
(the current folder when there is none). `--plan-file PATH` (relative to the current folder) reads another file and
implies `--plan`. Without a file:

```text
[lanq] Rejected: No lanq.yml in C:/src/app (the repository top). Write one (see docs/PLANS.md) or pass --plan-file PATH.
```

`lanq.yml` is per repository and is committed with it. `machines.yml` stays per person (see
[lanq.yml and machines.yml](#lanqyml-and-machinesyml)).

## A first plan

```yaml
version: 1
defaults:
  needs: [dotnet]
  timeout: 30
jobs:
  build:
    command: dotnet build -c Release -warnaserror
  unit:
    command: [dotnet, test, tests/Unit]
```

`build` and `unit` start side by side on one snapshot of the repository. Preview it, then run it:

```bash
lanq run --plan --dry-run
lanq run --plan
```

## Reference (version 1)

The file is strict: unknown keys, a duplicate key and an empty value are errors that name the key, the job and the
line, with a did-you-mean for a near miss. YAML anchors and aliases (`&name`, `*name`) and merge keys (`<<`) are
refused too: put values several jobs share under `defaults:`, or write them out. At most 256 KB, one YAML document.

### Top level

| Key | Required | Notes |
| --- | --- | --- |
| `version` | yes | The number `1`. A newer version is refused with "update lanq". |
| `defaults` | no | Values every job gets unless it sets its own: `dir`, `needs`, `mem`, `size` or `min_size`, `on`, `timeout`. |
| `jobs` | yes | A mapping of job names to jobs, 1 to 32. |

Job names start with a letter or digit and hold letters, digits, `_` and `-` (at most 32 characters); they must
differ ignoring case.

### Job keys

Each key is the `lanq run` option of the same name, with the same rules, ranges and messages.

| Key | `lanq run` option | Notes |
| --- | --- | --- |
| `command` | `-- CMD...` | Required. A list (exact argv) or a plain string; see [Command: list or string](#command-list-or-string). |
| `dir` | `--dir` | Relative to the repository top; `.` (the default) is the top. |
| `needs` | `--needs` | A list, or a comma-separated string (`needs: dotnet, linux`). Every tag is required. |
| `mem` | `--mem` | GB of memory available now, per job (per shard). |
| `size` | `--size` | A list of classes: `[light, medium]`. |
| `min_size` | `--min-size` | One class: `heavy`. Not together with `size`. |
| `on` | `--on` | Machine names from your `machines.yml`; see the note under [lanq.yml and machines.yml](#lanqyml-and-machinesyml). |
| `timeout` | `--timeout` | Minutes, 1 to 10080 (default 240). |
| `shards` | `--shards` | A number (1 to 64) or `auto`. |
| `target_minutes` | `--target-minutes` | With `shards: auto`. |
| `shard_retries` | `--shard-retries` | 0 to 3; only re-runs a shard whose machine stopped answering. |
| `fail_fast` | `--fail-fast` | `true`: the first failing shard of this job cancels its other shards. Needs `shards`. |
| `fetch` | `--fetch` | A pattern or a list of patterns, as `--fetch`. |
| `after` | (none) | Job names this job starts after; see [Ordering](#ordering-after). |

**Defaults are replaced, not merged.** A job's own value replaces the default as a whole: `needs: [node]` on a job
drops `needs: [dotnet]` from `defaults:` (write `needs: [dotnet, node]` for both). `size` and `min_size` are one
setting, so either one on a job drops both defaults.

**No machine settings.** `lanq.yml` has no keys for hosts, `ssh`, keys, `env`, `path_prepend`, `wake`, `priority`,
`work_root`, `max_jobs` or `policy`. Writing one is an unknown-key error that points to `machines.yml`.

### Command: list or string

- **List**: the exact arguments, passed as they are, with no shell. Use it whenever an argument has quotes, spaces,
  `|`, `&`, `*`, `$` or other special characters:

  ```yaml
  command: [dotnet, test, tests/Integration, --filter, "Category=Serial"]
  ```

- **String**: split on spaces. Only for plain words (letters, digits and `_ . / : = , + @ % -`) plus the literal
  `{shard}` and `{shards}`. Anything else is refused with "write command as a list (quotes, $ and other shell
  characters are not interpreted)".

  ```yaml
  command: npx jest --shard={shard}/{shards}
  ```

Neither form is ever run through a shell: `&&`, pipes, redirections and `$VAR` mean nothing. Put such steps in a small
committed script and allowlist that.

YAML quoting tips:

- Quote an item that starts with `{ [ * & ! # | > % @` or a backtick (unquoted, `*x` and `&x` are YAML aliases and
  anchors, which a plan file refuses), or that contains a colon followed by a space, or a space followed by `#`.
- In a flow list (`[...]`) also quote an item that contains `{`, `}`, `[`, `]` or `,`:
  `[npx, jest, "--shard={shard}/{shards}"]`. A block list (`- item` lines) avoids most of this.
- Quotes are YAML's, not the shell's: `"Category=Serial"` reaches the program as `Category=Serial`.

On Windows, `dotnet test --filter "A|B"` works as a list, because `dotnet` is a native `.exe` (the worker checks it
before starting anything; see the README's Limitations):

```yaml
  unit-and-arch:
    command: [dotnet, test, --filter, "Category=Unit|Category=Arch"]
```

The same filter as a string is refused (`|` is not a plain word).

### Ordering: after

Jobs without `after:` start at once, side by side, as free slots allow. A job with `after: [a, b]` starts when `a` and
`b` have both ended `Completed`; if either did not, it is `Skipped` ("not started: integration ended Failed"). `after:`
must name jobs of the file, not the job itself, and must not form a cycle (the cycle is reported).

Among jobs that are ready, lanq starts the one with the most expected work first (from earlier runs in your
`jobs.jsonl`), then jobs without history in file order. The next job starts once the previous one holds its first
slot or is waiting (at most 30 seconds apart).

### Jobs share nothing

Every job gets its own throw-away folder, maybe on another machine, from the same snapshot. `after:` only orders jobs:
it does not pass build output, test results or files from one job to the next. A job that needs a build builds it
itself (`dotnet test` builds first). Use `fetch:` to bring files back to your PC.

## A mixed pool

A pool of three small machines (`micro-01` .. `micro-03`, 4 to 8 GB) and two bigger ones (`big-01`, 32 GB, and
`build-01`, 16 GB). Builds and fast tests go to the small machines; the integration suite goes to the big ones,
sharded across two; serial tests run last, alone:

```yaml
version: 1
defaults:
  needs: [dotnet]
  timeout: 30
jobs:
  build:
    command: [dotnet, build, -c, Release, -warnaserror, -m:1]
    size: [light, medium]
  unit:
    command: [sh, scripts/lanq/test-shard.sh, tests/Unit]
    size: [light, medium]
    shards: auto
    target_minutes: 5
  arch:
    command: [dotnet, test, tests/Architecture, -m:1]
    size: [light, medium]
  integration:
    command: [sh, scripts/lanq/test-shard.sh, tests/Integration]
    min_size: heavy
    shards: 2
    timeout: 90
    fetch: ["TestResults/**/*.trx"]
  serial:
    command: [dotnet, test, tests/Integration, --filter, "Category=Serial"]
    min_size: heavy
    timeout: 60
    after: [integration]
```

`scripts/lanq/test-shard.sh` is the shard helper from [SHARDING.md](SHARDING.md#net); like every command it must be in
`policy.allowed_commands`. `min_size: heavy` works without naming machines: on this pool `big-01` and `build-01` probe
heavy. To keep heavy jobs off a 16 GB laptop someone works on, its owner gives it `size: medium` in `machines.yml`.

What a run prints (job lines are prefixed `[job machine]`, shards `[job i/N machine]`):

```text
[lanq] plan lanq.yml: 5 job(s); order: integration, unit, build, arch, serial
[integration 1/2 big-01] ...
[unit 2/3 micro-02] ...
[build micro-01] ...
[lanq] job          shards  machine(s)          status      exit   duration
[lanq] integration  2       big-01,build-01     Failed         1   1210.4s
[lanq] unit         3       micro-02,micro-03   Completed      0    212.0s
[lanq] build        -       micro-01            Completed      0     84.2s
[lanq] arch         -       micro-01            Completed      0     61.7s
[lanq] serial       -       -                   Skipped        -      0.0s  (not started: integration ended Failed)
[lanq] Next: 1 job(s) failed: integration (shard 2/2 exited 1, log: ...); read their logTail or logs and fix the code
[lanq] log: /home/me/.local/state/lanq/logs/20261109-221503-a1b2c3.log (integration 2/2)
[lanq] Failed: 3 of 5 job(s) completed; integration: Failed: ...
```

The last line is always `[lanq] <Status>: ...`.

## Patterns

### A guard job

A plan cannot tell whether a test filter or a shard script still matches the tests you meant. A cheap guard job
catches the usual mistakes before they pass silently: a script you commit that fails when a filter matches nothing,
or when the shards together list fewer tests than the whole suite.

```yaml
  guard:
    command: [sh, scripts/lanq/check-test-split.sh]
    size: [light, medium]
```

Make the later jobs depend on it (`after: [guard]`) when they are expensive, or let it run alongside when they are not.

### Make "no tests ran" fail

A job that runs zero tests usually exits 0 and shows `Completed`. Make it fail instead:

- .NET: `dotnet test` with a filter that matches nothing prints "No test matches the given testcase filter" and exits
  0. Check the count in your shard script, or with the Microsoft.Testing.Platform runner pass
  `--minimum-expected-tests 1`.
- Jest: leave out `--passWithNoTests` (Jest fails on no tests by default).
- pytest: exits 5 when no tests were collected; do not mask it.
- Go: `go test` passes a package with no tests; list the packages in your shard script and fail on an empty list.

## Options

| Option | What it does |
| --- | --- |
| `--plan` | Run `lanq.yml` at the repository top. |
| `--plan-file PATH` | Read this file instead (implies `--plan`). |
| `--only a,b` | Run only these jobs. An unknown name is refused (exit 2) with the list of jobs. An `after:` job that is not selected is dropped from the order, with a note: `[lanq] --only: serial starts without waiting for integration (not selected)`. |
| `--dry-run` | Say what the plan would do now, and do none of it. |
| `--fail-fast` | The first job that does not complete cancels the running jobs (cleaned up, `Cancelled`) and skips the rest. |
| `--wait S` | Seconds to wait for a free machine. It counts only while no job of the plan holds a slot, so jobs waiting behind their siblings do not time out. |
| `--no-wake` | Never wake a sleeping machine for any job. |
| `--json [--log-tail N]` | One JSON object on stdout ([docs/JSON.md](JSON.md#lanq-run---plan---json)). |
| `--config FILE` | As for every command. |

The options a job sets in `lanq.yml` are refused with `--plan` (exit 2): a command after `--`, `--needs`, `--dir`,
`--mem`, `--timeout`, `--size`, `--min-size`, `--on`, `--shards`, `--shard-retries`, `--target-minutes` and the
`--fetch` options. `--only` and `--plan-file` are refused without a plan.

### --dry-run

The same file checks, command-line checks and snapshot scan as a run, one probe of the machines the jobs could use,
and the first wave dealt on one table of free slots. Nothing is sent, no slot is claimed, nothing is woken, and
`jobs.jsonl` is read, never written. It prints each job's equivalent `lanq run` line, so you can run one job by hand:

```text
lanq run --plan --dry-run: nothing was sent, no slot claimed, nothing woken
Plan:     lanq.yml (version 1): 5 job(s); order: integration, unit, build, arch, serial (longest first from history)
Snapshot: command lines and 2 uncommitted file(s) clean
integration  lanq run --needs dotnet --min-size heavy --shards 2 --timeout 90 --fetch 'TestResults/**/*.trx' -- sh scripts/lanq/test-shard.sh tests/Integration
             would run 2 shard(s): big-01 shard 1; build-01 shard 2; about 31 min of work (from history)
unit         lanq run --needs dotnet --size light,medium --shards auto --target-minutes 5 --timeout 30 -- sh scripts/lanq/test-shard.sh tests/Unit
             --shards auto would pick 3 (...); would run 3 shard(s): ...
build        lanq run --needs dotnet --size light,medium --timeout 30 -- dotnet build -c Release -warnaserror -m:1
             would wait for a slot this plan holds (its machines are taken by unit, or by other work)
arch         ...
serial       lanq run --needs dotnet --min-size heavy --timeout 60 -- dotnet test tests/Integration --filter Category=Serial
             pending: starts after integration
```

Each job is `WouldRun` (it would start now), `WouldWait` (every slot it could use is this plan's own), `Pending` (it
starts after other jobs) or `NoCapacity`. The dry run exits 0 when the plan would start, 75 when a first-wave job has
no machine and nothing of the plan to wait behind, 2 when refused.

## Status and exit code

Each job ends with a `lanq run` status (`Completed`, `Failed`, `Rejected`, `NoCapacity`, `InfraError`, `TimedOut`,
`WindowClosed`, `Cancelled`) or `Skipped` (never started: an `after:` job did not complete, `--fail-fast`, or Ctrl+C).

- The plan is `Completed` (exit 0) only when every selected job completed.
- Otherwise the worst job decides, earliest in launch order on a tie: `Failed` (that job's own exit code), then
  `Rejected` (2), `TimedOut` and `WindowClosed` (124), `InfraError` (70), `NoCapacity` (75), `Cancelled` (130).
- Before anything is sent, every job (and every shard of a fixed `shards: N`) is checked as its own `lanq run` line.
  One refusal refuses the whole plan (`Rejected`, 2, naming the job). A job no machine can ever take as configured
  ends the plan with `NoCapacity` (75, `retryable: false`) before the snapshot.
- Ctrl+C cancels and cleans up every started job: `Cancelled`, 130.

A suggested re-run (`nextSteps`, or the `Next:` line) is the same command with `--only` set to the jobs that did not
complete; it never changes anything else.

## Environment

Every job gets `LANQ_JOB` (its job id) and `LANQ_PLAN_JOB` (its name in `lanq.yml`); a sharded job also gets
`LANQ_SHARD` and `LANQ_SHARDS`. Your `jobs.jsonl` records `planJob` and `planRun` (shared by the jobs of one plan run).

## lanq.yml and machines.yml

| | `lanq.yml` | `machines.yml` |
| --- | --- | --- |
| Where | the repository, committed | your per-user config folder, never committed |
| Who writes it | the team (reviewed like code) | each developer |
| Holds | jobs: commands, needs, sizes, timeouts, order | machines, hosts, keys, `env`, `path_prepend`, hours, the allowlists |

`lanq.yml` comes with the repository, so lanq treats it as untrusted input: it can only say what a `lanq run` line can
say, and every job still has to pass your allowlist. A plan never adds machines, keys or commands.

Machine names are per person: `big-01` in your `machines.yml` may not exist in a colleague's. In a shared `lanq.yml`
prefer `needs:` and `size:` / `min_size:`, which travel. `on:` is for a plan only you use; an unknown name refuses the
plan (exit 2) with your configured names.
