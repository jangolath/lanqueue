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
| `plan_timeout` | no | Minutes, 1 to 10080: the whole plan's limit (0.10.0); `--plan-timeout` on the command line replaces it. See [Plan timeout](#plan-timeout-plan_timeout). |
| `defaults` | no | Values every job gets unless it sets its own: `dir`, `needs`, `mem`, `size` or `min_size`, `on`, `timeout`, `env`, `exclusive`, `baseline`, `baseline_ref`, `fetch_max`, `fetch_max_files` (the fetch limits apply only to jobs with `fetch:`). |
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
| `fetch_max` | `--fetch-max` | With `fetch:`. May only lower the 200 MB default (0.10.0); see [Fetch limits and folder](#fetch-limits-and-folder). |
| `fetch_max_files` | `--fetch-max-files` | With `fetch:`. May only lower the 500-file default (0.10.0). |
| `env` | `--env NAME=VALUE` | A mapping of `NAME: value`, one `--env` per entry (0.9.0). Only names in your `policy.allowed_env`; see [Environment variables: env](#environment-variables-env). |
| `exclusive` | `--exclusive` | `true`: the job takes every slot of its machine (0.9.0); see [Exclusive jobs](#exclusive-jobs). |
| `baseline` | `--baseline` | `true`: if the job fails, rerun it at the base commit and report whether the failure is new (0.9.0); see [Baseline](#baseline-is-the-failure-new). |
| `baseline_ref` | `--baseline-ref` | The branch or tag to compare with (implies `baseline: true`). A commit id is refused here; see [Baseline](#baseline-is-the-failure-new). |
| `after` | (none) | Job names this job starts after; see [Ordering](#ordering-after). |

**Defaults are replaced, not merged.** A job's own value replaces the default as a whole: `needs: [node]` on a job
drops `needs: [dotnet]` from `defaults:` (write `needs: [dotnet, node]` for both). `size` and `min_size` are one
setting, so either one on a job drops both defaults. `baseline` and `baseline_ref` combine as described under
[Baseline](#baseline-is-the-failure-new). **`env` is the exception:** it is merged by name (see below).

**No machine settings.** `lanq.yml` has no keys for hosts, `ssh`, keys, `path_prepend`, `wake`, `priority`,
`work_root`, `max_jobs` or `policy`. Writing one is an unknown-key error that points to `machines.yml`. `env:` here is
the job's own variables, never a machine's.

### Environment variables: env

```yaml
defaults:
  env:
    CONFIGURATION: Release
    TEST_FILTER: Category=Unit
jobs:
  unit:
    command: [dotnet, test, tests/Unit, -c, Release]
  nightly:
    command: [dotnet, test, tests/Unit, -c, Release]
    env:
      TEST_FILTER: Category=Slow
```

`unit` gets `--env CONFIGURATION=Release --env TEST_FILTER=Category=Unit`; `nightly` keeps `CONFIGURATION` from the
defaults and gets its own `TEST_FILTER`. Names are compared ignoring case, and the job's spelling wins. Each value is
taken as written (`3` is `"3"`, `true` is `"true"`); a list or mapping as a value is refused.

Each entry is checked as an `--env` flag (see the README's
[Job variables](../README.md#job-variables---env)):

- The name must be in **your** `machines.yml` `policy.allowed_env`. A plan file comes with the repository, so it can
  set only names you allowed; anything else refuses the plan (2), naming the job.
- Names on lanq's deny table (`NODE_OPTIONS`, `DOTNET_STARTUP_HOOKS`, `GIT_*`, `HOME`, `TEMP`, `LANQ_*` and more) are
  refused even when allowed.
- A value must be non-empty, without control characters, at most 4,096 characters (16,384 for all of a job's
  variables), and must not look like a secret. Never put secrets in `lanq.yml`.
- A machine whose own `env:` sets the same name is not used for that job (`env_conflict`), and neither is a Windows
  machine the values make the command too long for (`windows_command_too_long`).

### Exclusive jobs

```yaml
  perf:
    command: [dotnet, test, tests/Perf]
    exclusive: true
    on: [big-01]
```

The job takes every slot of its machine, so nothing else of the plan, or of anyone else, runs there meanwhile. It
waits until the machine is idle; there is no reservation, so on a machine that is never idle it waits out `--wait`
(`NoCapacity`, 75). The dry run counts the machine as full, so a sibling job is dealt elsewhere or waits. With
`shards:` each shard takes a whole machine.

### Baseline: is the failure new?

```yaml
defaults:
  baseline: true          # or baseline_ref: origin/release-2.1
```

A job with `baseline: true` that ends `Failed` is run again at the base commit (the merge-base of HEAD and the default
branch, or of HEAD and `baseline_ref`) once the whole plan has ended, and its result gets a verdict: `new`,
`preexisting`, `inconclusive` or `skipped`. Only failed jobs rerun (for a sharded job, only its failed shards), never
a job `Skipped` after them, and nothing reruns after Ctrl+C (each failed job that asked gets an `inconclusive`
"cancelled (Ctrl+C) before it started"). A rerun waits briefly for the machine the job failed on (its lines start
`[baseline unit]` or `[baseline unit 2/4]`). The verdict never changes the plan's status or exit code. See the
README's [Is this failure new?](../README.md#is-this-failure-new---baseline).

**`baseline_ref` names a branch or tag.** It must resolve under `refs/heads/`, `refs/remotes/` or `refs/tags/`
(`origin/main`, `release-2.1`, `v1.2.0`). A commit id, `HEAD` or `*_HEAD`, and revision characters
(`~ ^ : @ { } \ * ? [` and space) are refused before anything is sent, in the dry run too:

```text
lanq.yml: job 'unit' (line 12): baseline_ref must name a branch or tag (a commit can be given on the command line with --baseline-ref)
```

A short name must exist under exactly one of those: a name that is both a branch and a tag is refused with
`baseline_ref NAME is both a branch and a tag; write refs/heads/NAME or refs/tags/NAME`, and one that matches more
than one ref with `baseline_ref NAME names more than one ref (refs/heads/NAME, refs/remotes/NAME); write one of them`.
A full `refs/heads/NAME` or `refs/tags/NAME` is accepted. git's own short forms `heads/main`, `tags/v1.0` and
`remotes/origin/main`, which git itself would accept, are refused with the full ref to write instead:
`baseline_ref heads/main: write refs/heads/main` (and `write refs/tags/v1.0`, `write refs/remotes/origin/main`). The baseline's `ref` and its label show the name as you
wrote it.

The plan file comes with the repository. If it could name any commit, it could make lanq send that commit's tree to
a worker, which on an archive worker means any old version of the files. You can still compare with a commit by
running one job by hand with `--baseline-ref <sha>`.

**How `baseline` and `baseline_ref` combine.** The job's own keys decide first, then `defaults:`:

| On the job | In `defaults:` | Result |
| --- | --- | --- |
| `baseline: false` and `baseline_ref:` | any | Error: `lanq.yml: job 'x' (line N): baseline_ref has no effect with baseline: false; drop one.` |
| `baseline: false` | any | No baseline (an inherited `baseline_ref` is dropped quietly). |
| `baseline_ref: REF` | any, even `baseline: false` | `--baseline-ref REF`. |
| `baseline: true` | `baseline_ref: REF` | `--baseline-ref REF`. |
| `baseline: true` | `baseline: false` and `baseline_ref: REF` | `--baseline` (the default's ref is dropped). |
| `baseline: true` | nothing | `--baseline`. |
| neither | `baseline: false` | No baseline. |
| neither | `baseline_ref: REF` or `baseline: true` | `--baseline-ref REF`, or `--baseline`. |

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

### Plan timeout: plan_timeout

A job's `timeout:` limits that job. `plan_timeout: M` (or `lanq run --plan --plan-timeout M`, which replaces the file's
value) limits the whole plan, in minutes from 1 to 10080 (0.10.0):

```yaml
version: 1
plan_timeout: 60
jobs:
  ...
```

The clock starts after the command-line checks and the snapshot. It covers waiting for slots, the jobs and the baseline
reruns. When it runs out, lanq prints `[lanq] plan_timeout: 60 min reached; stopping 2 started job(s)` and then:

- **Running jobs** are stopped and cleaned up, as `--fail-fast` stops them. Each one ends `TimedOut` (124) with "plan_timeout
  60 min reached; cancelled and cleaned up". `jobs.jsonl` records it as `TimedOut`.
- **Jobs waiting for a slot** end `TimedOut` with "plan_timeout reached while waiting for a slot". Nothing was claimed,
  so nothing needs cleaning up.
- **Jobs not started yet** (behind `after:` or the stagger) are `Skipped`: "not started: plan_timeout reached".
- **Baseline reruns not finished** are `inconclusive` ("plan_timeout reached").

The plan is then `TimedOut` (124) unless a `Failed` or `Rejected` job decides it. This holds even when nothing was
running when the time ran out, for example between `after:` waves or while every job was waiting. Each job's own
`timeout:` still applies. `--wait` cannot outlast the plan's limit. Ctrl+C before the limit still ends the plan
`Cancelled` (130). If the limit comes while the last job is only cleaning up, it stops nothing: lanq adds
`[lanq] plan_timeout: nothing was stopped; every job had already ended` and the plan keeps its own status, without
`planTimedOut`. After `--fail-fast` the limit still bounds the baseline reruns that follow. If it comes while
`--fail-fast` is still stopping the jobs, those jobs stay `Cancelled`, lanq prints `[lanq] plan_timeout: 60 min reached
while the jobs were already being stopped (--fail-fast or an error); no baseline rerun will start`, and every baseline
is `inconclusive` ("plan_timeout reached before it started").

`planTimedOut` is `true` whenever the limit came due before the plan finished its work: when it stopped a job or a
baseline, and in every case where it came during a `--fail-fast` hold (with or without baselines to cut short). The
one exception is a limit that came while the last job was only cleaning up: nothing was left to do, so the plan is
not flagged and lanq adds the "nothing was stopped" line (never after a hold). The flag is information: `Failed` and
`Rejected` still decide the status and exit code.

`--json` adds `planTimeoutMinutes` (whenever a limit is set) and `planTimedOut: true` (when it was reached). The dry
run shows the limit on a `Timeout:` line. It warns when history's expected critical path (the longest `after:` chain
of expected work) is longer than the limit.

**Refused:** `--plan-timeout` without `--plan` (exit 2; a single run's limit is `--timeout`). A value outside 1 to
10080 in either place is refused with the same words.

**Agents:** a plan that timed out gets a `fix_code` step. Find where the plan was slow from the logs. Raising
`plan_timeout` in lanq.yml or passing a larger `--plan-timeout` is the developer's decision, never a way to make a
plan pass.

### Narrowing a plan from the command line

`--on`, `--size` and `--min-size` work with `--plan` (0.10.0). They **narrow** every selected job and never widen one:

- A job's effective `on` is its own `on:` intersected with `--on`, or `--on` alone when the job has none.
- A job's effective size is the size classes both its `size:`/`min_size:` and the command line allow.

```sh
lanq run --plan --on big-01 --json --no-wake      # every job only on big-01
lanq run --plan --min-size medium --dry-run       # each job's narrowed run line
```

The dry run prints each job's narrowed `lanq run` line. Without these options the plan behaves exactly as before.

**Refused:**

- An `--on` name that is not in your `machines.yml` is refused before any network (exit 2).
- A job the narrowing leaves with no machine ends the whole plan before the snapshot, with nothing sent:
  `NoCapacity` (75, `retryable: false`), for example "--on big-01 leaves job 'unit-windows' no machine (its on:
  [micro-01])". The other jobs are `Skipped`. `nextSteps` are `ask_developer`, then `fix_command`: the same command
  with `--only` set to the jobs that can still run. A job that starts after a narrowed-out job (directly or not) is
  left out too, so nothing runs without its prerequisite. That `--only` replaces any `--only` you gave, and the
  narrowing is kept. Because it leaves jobs out, the step is not `safeToRunUnattended` (its `--dry-run` is).

**Agents:** an agent may narrow a plan. It never edits lanq.yml (its `on:`, `size:` or `min_size:`) to widen a job so
that a narrowed plan passes. It asks the developer before running the `fix_command` rerun, and then reports that
the left-out jobs did not run.

### Fetch limits and folder

A job with `fetch:` brings files back to your PC: at most 500 files and 200 MB by default, under `.lanq/artifacts` at
the repository top. Since 0.10.0:

- `fetch_max` and `fetch_max_files` (on a job, or in `defaults:` for the jobs that fetch) are `--fetch-max` and
  `--fetch-max-files`. They may only **lower** the defaults. A larger value is refused (exit 2): "lanq.yml may lower
  fetch limits (at most 200M / 500 files); a larger limit is the developer's choice: lanq run --plan --fetch-max 1G".
- There is no `fetch_dir` key. A repository never picks where files land on your disk, and the key is refused with a
  hint to `--fetch-dir`.
- On the command line, `lanq run --plan --fetch-dir DIR`, `--fetch-max SIZE` and `--fetch-max-files N` apply to every
  selected job that fetches. They replace the file's values, up to the 2 GB and 5,000-file ceiling. They are refused
  (exit 2) when no selected job fetches.

There is no plan-wide total. At worst a plan writes the number of fetching jobs times the per-job limit: 32 jobs at
the defaults is 6.4 GB, and at `--fetch-max 2G` it is 64 GB.

**Agents:** an agent never raises `--fetch-max` or `--fetch-max-files`, and never adds `fetch_max` to lanq.yml, to get
a plan's files through. A `fetchSkipped` entry over a limit is reported to the developer. Fetch problems never change
a job's exit code.

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
    command: [sh, scripts/lanq/check-test-split.sh, tests/App.Tests/App.Tests.csproj, "4"]
    size: [light, medium]
```

`check-test-split.sh` and its PowerShell twin `check-test-split.ps1` are in [SHARDING.md](SHARDING.md#net); lanq's own
[`lanq.yml`](../lanq.yml) runs the PowerShell one with the same N as its Windows shards. A plan file has no conditions,
so a job for an OS your pool lacks can never run and fails the plan: keep such jobs commented out, as that file does
with its Linux job.

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
| `--on m[,m]`, `--size C[,C]`, `--min-size C` | Narrow every job, never widen one (0.10.0); see [Narrowing](#narrowing-a-plan-from-the-command-line). |
| `--plan-timeout M` | The whole plan's limit in minutes (0.10.0); replaces `plan_timeout:`. See [Plan timeout](#plan-timeout-plan_timeout). |
| `--fetch-dir DIR`, `--fetch-max SIZE`, `--fetch-max-files N` | For every job that fetches (0.10.0), up to 2 GB and 5,000 files; see [Fetch limits and folder](#fetch-limits-and-folder). |
| `--only a,b` | Run only these jobs. An unknown name is refused (exit 2) with the list of jobs. An `after:` job that is not selected is dropped from the order, with a note: `[lanq] --only: serial starts without waiting for integration (not selected)`. |
| `--dry-run` | Say what the plan would do now, and do none of it. |
| `--fail-fast` | The first job that does not complete cancels the running jobs (cleaned up, `Cancelled`) and skips the rest. |
| `--wait S` | Seconds to wait for a free machine. It counts only while no job of the plan holds a slot, so jobs waiting behind their siblings do not time out. |
| `--no-wake` | Never wake a sleeping machine for any job. |
| `--json [--log-tail N]` | One JSON object on stdout ([docs/JSON.md](JSON.md#lanq-run---plan---json)). |
| `--config FILE` | As for every command. |

The options a job sets in `lanq.yml` are refused with `--plan` (exit 2): a command after `--`, `--needs`, `--dir`,
`--mem`, `--timeout`, `--shards`, `--shard-retries`, `--target-minutes`, `--fetch`, `--env`, `--exclusive`,
`--baseline` and `--baseline-ref`. `--only`, `--plan-file` and `--plan-timeout` are refused without a plan.

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
- Otherwise the worst job decides, earliest in lanq.yml on a tie: `Failed` (that job's own exit code), then
  `Rejected` (2), `TimedOut` and `WindowClosed` (124), `InfraError` (70), `NoCapacity` (75), `Cancelled` (130).
- Before anything is sent, every job (and every shard of a fixed `shards: N`) is checked as its own `lanq run` line.
  One refusal refuses the whole plan (`Rejected`, 2, naming the job). A job no machine can ever take as configured
  ends the plan with `NoCapacity` (75, `retryable: false`) before the snapshot.
- `--on`, `--size` or `--min-size` that leave a job no machine end the plan before the snapshot: `NoCapacity` (75,
  `retryable: false`); see [Narrowing](#narrowing-a-plan-from-the-command-line).
- When `plan_timeout` is reached, the plan is `TimedOut` (124) unless a `Failed` or `Rejected` job decides it; see
  [Plan timeout](#plan-timeout-plan_timeout).
- Ctrl+C cancels and cleans up every started job: `Cancelled`, 130.

A suggested re-run (`nextSteps`, or the `Next:` line) is the same command with `--only` set to the jobs that did not
complete (after a narrowing, the jobs that can still run). It replaces any `--only` you gave and never changes
anything else.

## Environment

Every job gets `LANQ_JOB` (its job id) and `LANQ_PLAN_JOB` (its name in `lanq.yml`); a sharded job also gets
`LANQ_SHARD` and `LANQ_SHARDS`. A job with `env:` gets those variables too, set after the machine's `env:` and before
the `LANQ_*` ones. Your `jobs.jsonl` records `planJob`, `planRun` (shared by the jobs of one plan run), `planFile`
(the plan file's full path on your PC, 0.10.0), and the names (never the values) of a job's `env:` as `envNames`.

`lanq stats` adds a "Plan jobs" table (`planJobs` in `--json`), one row per plan file and job name, one run per plan
run: completed when all its shards were recorded and each one's last attempt completed, failed when any failed, other otherwise. A run's wall time
runs from its first start to its last end. The plan file is the full path, so clones of one repository in different
folders are separate rows. Records written before 0.10.0 have no `planFile` and group under none.

## lanq.yml and machines.yml

| | `lanq.yml` | `machines.yml` |
| --- | --- | --- |
| Where | the repository, committed | your per-user config folder, never committed |
| Who writes it | the team (reviewed like code) | each developer |
| Holds | jobs: commands, needs, sizes, timeouts, order, the job's own `env`, `exclusive`, `baseline`, lower fetch limits; the plan's `plan_timeout` | machines, hosts, keys, each machine's `env`, `path_prepend`, hours, the allowlists (`allowed_commands`, `allowed_directories`, `allowed_env`) |

`lanq.yml` comes with the repository, so lanq treats it as untrusted input: it can only say what a `lanq run` line can
say, and every job still has to pass your allowlist. A plan never adds machines, keys or commands, and its `env:` can
set only the names your `policy.allowed_env` lists. It may lower the fetch limits but never raise them, and it never
picks the folder fetched files go to. If a plan job is refused for an `env:` name, add the name to
`allowed_env` yourself only if jobs may set it; a coding agent must never do that for you.

Machine names are per person: `big-01` in your `machines.yml` may not exist in a colleague's. In a shared `lanq.yml`
prefer `needs:` and `size:` / `min_size:`, which travel. `on:` is for a plan only you use; an unknown name refuses the
plan (exit 2) with your configured names.
