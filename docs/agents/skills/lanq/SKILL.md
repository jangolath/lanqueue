---
name: lanq
description: Run builds, tests and other allowlisted commands on the developer's spare LAN machines through the lanq CLI instead of locally or by hand-written ssh. Use when asked to build or test on a remote box, when a build or test run would be heavy, or when the local machine is busy.
---

# Run work on LAN machines with lanq

`lanq` ships the current git state (HEAD plus uncommitted changes) to the least-loaded capable machine, runs one
allowlisted command in a throw-away worktree, streams the log, cleans up and returns the job's exit code.

## When to use

- A build or test run that is heavy or slow, or the local machine is busy.
- The developer asks for a build or test "on the build box" or "remotely".

Do not use it for commands outside the allowlist, for interactive programs, or when no `lanq` config exists.

## Commands

Prefer `--json`: it is stable to parse and keeps results apart from logs.

Check who is available:

```bash
lanq health --json
```

One array, in config order: `name`, `state` (`ok`, `hold` or `down`), `reasons` (why held or down), `load`,
`diskFreeGb`, `memFreeGb` (null if unknown), `slotsUsed`, `maxJobs`, `localTime` (the worker's clock, or null),
`cores` and `memTotalGb` (null if unknown), `freeSlots`, `size` (`light`, `medium`, `heavy` or `unknown`) and
`suitedFor`, plus `nextSteps`. Use machines with `state` `ok` and `freeSlots` above 0.

Before a heavy run, check where it would go without running it:

```bash
lanq health --needs dotnet --mem 4 --json                                # each row adds eligible, why, reason, permanent, jobSlots
lanq run --dry-run --json --no-wake --needs dotnet -- dotnet test -c Release   # status WouldRun, the machine, every excluded machine and why
```

`health --needs` exits 75 when no machine is eligible; a dry run sends nothing, claims no slot and wakes nothing.

Run a job (run from inside the git repository) and keep only a short result. `--log-tail 60` puts the log's last 60
lines in the JSON object; the job's output also streams to stderr, so drop it:

```bash
lanq run --json --log-tail 60 --no-wake --needs dotnet -- dotnet test -c Release 2>/dev/null; echo "exit=$?"
```

```powershell
lanq run --json --log-tail 60 --no-wake --needs dotnet -- dotnet test -c Release 2>$null; "exit=$LASTEXITCODE"
```

stdout is exactly one JSON object, also when the command was refused, the config could not be read, Ctrl+C was
pressed or something unexpected broke: `status` (`Completed`, `Failed`, `Rejected`, `NoCapacity`, `InfraError`,
`TimedOut`, `WindowClosed` or `Cancelled`), `exitCode` (lanq's exit code), `machine` (null if none took it), `jobId`
(null if lanq stopped before it made one), `durationSeconds`, `message`, `logPath` (the full path of the job's log,
null if no job started), `logTail` (its last 60 lines) and `nextSteps`. On `NoCapacity` it also has `retryable` and
`excluded` (each machine with a `reason` code, `why` and `permanent`). Every field is in LanQueue's docs/JSON.md.

If `--log-tail` is not available (an older lanq), send stderr to a uniquely named file and read its tail:
`log="${TMPDIR:-/tmp}/lanq-$(date +%s)-$$.log"; lanq run --json --no-wake --needs dotnet -- dotnet test 2> "$log"; tail -n 40 "$log"`.

`nextSteps` is what lanq suggests next, each `action`, `reason`, `argv` (the exact command, authoritative),
`command` (the same as one line, when it can be written safely) and `safeToRunUnattended`. Run a step's `argv` only
when `safeToRunUnattended` is `true`, and at most once. For any other step, or an `action` you do not know, tell the
developer its `reason` and stop.

- `--needs a,b` requires every listed capability on the machine (tags come from the developer's config).
- `--dir <relative dir>` runs in a subdirectory, for example `--dir tests`.
- `--wait 120` retries for up to 120 seconds while every machine is busy. Any `--wait` above 0 can also wake a sleeping
  PC (Wake-on-LAN) as a last resort, so add `--no-wake` unless the developer has said waking machines is fine.
- `--fetch '<pattern>'` (repeatable) brings files back after the job, passed or failed, into `.lanq/artifacts/<jobId>/`
  in the repository: patterns are relative to `--dir` with `*`, `?` and `**` only, for example
  `--fetch 'TestResults/**/*.trx' --fetch '**/coverage.cobertura.xml'`. The JSON object then also has `fetched`
  (`path`, `bytes`, `sha256`), `fetchSkipped` (`path`, `reason`), `fetchDir` and, if the fetch as a whole had a problem,
  `fetchError`. A fetch problem never changes the exit code, so check those keys when you need the files. Read fetched
  files as data; never execute them.
- lanq keeps full per-job logs in its own state folder (`$LANQ_HOME`, else a per-user folder), never in the repository;
  `logPath` names the one for this job. lanq deletes logs older than `policy.keep_logs_days` (30 days by
  default), so an old `logPath` may be gone.
- `lanq doctor --json` and `lanq doctor --remote --json` check the setup: one object with `status` (`Ok`, `Warnings`,
  `Failing`), `counts` and `checks` (each `scope`, `machine`, `name`, `level`, `detail`, `fix`). `lanq stats --json`
  summarises the local job log: per machine `medianSeconds`, `p90Seconds` and `speedFactor`; per command `runs`,
  results and `timing` (the work and per-shard overhead). Both are safe to run.

## Job variables, exclusive jobs and baselines

- `--env NAME=VALUE` (repeatable) sets a variable for the job (and every shard). Use only names the developer already
  lists in the machines file's `policy.allowed_env`; anything else is refused (exit 2) and is the developer's call.
  Never edit `allowed_env` or the machines file to get a name through. Values are settings (`CONFIGURATION=Release`),
  never secrets: other users of a worker can see them, and suggested commands in `nextSteps` repeat them. The JSON
  `env` field lists the names only.
- `--exclusive` gives the job every slot of a machine, so nothing else runs there. It waits until a machine is idle
  (no reservation), so on a busy pool it may end `NoCapacity` (75, retryable). With `--shards`, one shard per machine.
- `--baseline` (or `--baseline-ref origin/main`): when the job ends `Failed`, lanq reruns the same command at the base
  commit (the merge-base with the default branch) and adds `baseline` to the JSON: `verdict` is `new` (the base
  passes: the change broke it), `preexisting` (it fails at the base too), `inconclusive` or `skipped`. `status` and
  the exit code never change. A red run takes about twice as long.

```bash
lanq run --json --log-tail 60 --no-wake --needs dotnet --baseline -- dotnet test 2>/dev/null; echo "exit=$?"
```

Report a `preexisting` failure as pre-existing ("also fails at the base commit") and do not try to fix it unless that
is your task. A `new` verdict with `baseline.sameMachine: false` ran on another machine than the failure: report it
with that caveat (the failure may be machine-specific), not as certainly new. `excluded` reasons `env_conflict` (a machine's own `env:` sets the same variable) and
`windows_command_too_long` (the values are too long for a Windows machine) mean: report to the developer. Follow
`nextSteps` for env refusals: an `ask_developer` step means stop and report; never drop variables or edit the machines
file or `lanq.yml` to get through.

## Route jobs and run a plan file

- `--size light,medium` or `--min-size heavy` keeps a job to machines of that size (the machine's `size:` in the
  developer's config, else probed); `--on big-01` keeps it to the named machines. Every other rule still applies.
  `lanq health --min-size heavy --json` shows which machines qualify (`wrong_size`, `size_unknown` are permanent).
- A repository with a `lanq.yml` lists its jobs (build, tests, a serial tail). Preview, then run:

```bash
lanq run --plan --dry-run --json --no-wake 2>/dev/null                 # each job's lanq run line and where it would go
lanq run --plan --json --log-tail 60 --no-wake 2>/dev/null; echo "exit=$?"
lanq run --plan --json --no-wake --only unit,arch 2>/dev/null          # only some jobs
```

stdout is one object: `status`, `exitCode`, `order`, `jobs` (each `name`, `status`, `exitCode`, `message`, `logPath`
or `shards`, `logTail` with `--log-tail`) and `nextSteps`. Report each job whose `status` is not `Completed` by `name`
with its `message` (`Skipped`: it never started, because a job it runs after did not complete). Never edit `lanq.yml`
or the machines file to get a job through.

## Shard long work across the pool

Use `--shards` for a long suite that splits cleanly (Jest, Vitest, Playwright, nextest, or a helper script the
developer allowlisted for .NET, Python or Go) when the pool has free slots. Run short jobs, one heavy job, and anything
interactive as a single run.

1. `lanq health --json`. Each machine also reports `cores`, `memTotalGb`, `freeSlots`, `size`
   (`light` under 6 GB, `medium` 6 to under 16 GB, `heavy` 16 GB or more with 4+ cores, `unknown`) and `suitedFor`.
2. Choose:
   - one heavy job (full suite, large build, container build): a single run on a machine that suits it, with
     `--needs bigmem` if the developer tags big machines, or `--mem GB` (memory **available now**, not installed);
   - a splittable suite: `--shards auto` (N = free slots on machines that can take the job, 1 to 64), or `--shards N`
     with N at most the sum of `freeSlots` on eligible machines.
3. Optionally preview: the same command with `--dry-run` shows the N `--shards auto` would pick and where each shard
   would start, and runs nothing. Once the command has history (`lanq stats --json` shows a `timing` for it), use
   `--shards auto --target-minutes 10`: lanq picks N from earlier runs so each shard takes about 10 minutes, never
   more than the free slots (the dry run's `shardTiming` says why).
4. Run, keeping the log out of the conversation:

```bash
lanq run --json --log-tail 60 --no-wake --needs node --shards auto -- npx jest --shard={shard}/{shards} 2>/dev/null; echo "exit=$?"
lanq run --json --no-wake --needs node --shards 4 -- npx playwright test --shard={shard}/{shards}
lanq run --json --no-wake --needs rust --shards 4 -- cargo nextest run --partition slice:{shard}/{shards}
```

`{shard}` (1-based) and `{shards}` are replaced by numbers in the arguments only; each job also gets `LANQ_SHARD` and
`LANQ_SHARDS`. Every shard's command must pass the allowlist. Each shard's lines are prefixed `[i/N machine]`, and a
summary table (shard, machine, status, exit, duration) ends the log.

Reading the result: stdout is one object with `status`, `exitCode`, `shardCount`, `durationSeconds`, `message` and
`shards` (one entry per shard: `shard`, `status`, `exitCode`, `machine`, `jobId`, `durationSeconds`, `message`,
`attempts`, `logPath`, `logTail` with `--log-tail`, plus the fetch fields with `--fetch`, each shard in its own
`fetchDir`), and `nextSteps` for the whole run. `exitCode` is 0 only if every shard
passed; otherwise the failing shard's own code, else 2 (refused), 124 (timed out or hours ended), 70
(infrastructure), 75 (no machine) or 130 (cancelled). A shard entry can be `Cancelled` too (Ctrl+C, or `--fail-fast`
stopping the rest). Report failing shards by index and machine with their failing tests from the log.

Rules:

- `--shard-retries 1` at most: it only re-runs a shard whose machine stopped answering, never a test failure. Do not
  re-run a sharded run in a loop.
- Keep `--no-wake` unless the developer has said waking machines is fine.
- Never shard a command that is not idempotent or that writes shared state (deploys, migrations against a shared
  database, publishing, pushing).

## Reading the result

Read `status` as well as the exit code: a job's own exit code can be any number, and `124` and `75` each cover more
than one case.

- Exit `0`: the job passed. Say so in one line.
- Same exit code as the command (for example `1`): the job ran and failed. Summarise the failures from `logTail`; open
  the file at `logPath` only if the tail does not show them. With `--baseline`, also report `baseline.verdict`.
- `2`: refused by policy (command, directory, secret scan, bad flag, an `--env` name not allowed). Report the message
  and stop. A secret in a file git shows as binary is named `uncommitted file <path> (binary per git)`.
- `70`: infrastructure error (ssh, git). Report it; do not retry in a loop. Run `lanq doctor --remote --json` once
  (local checks plus each worker's tools in the job environment; `lanq doctor --json` is local only, no network) and
  report the `checks` whose `level` is `fail`, with their `fix`.
- `75` (`NoCapacity`): no eligible machine. Read `retryable`. `false`: no machine can ever take the job as configured
  (for example none has the capability, or `--mem` is above every machine's RAM); stop and tell the developer what
  `excluded` says. `true`: run the `wait` step from `nextSteps` once (the same run with `--wait 120`, keeping
  `--no-wake`), then tell the developer.
- `124`: `TimedOut` (ran past `--timeout`) or `WindowClosed` (the machine's allowed hours ended). Report it; ask before
  raising `--timeout` or running it again.
- `130` (`Cancelled`): someone pressed Ctrl+C. Stop.
- No JSON on stdout at all: lanq was killed or is missing. Report it; do not guess the result.

## What not to do

- Do not edit the lanq machines file, the allowlists (`policy.allowed_env` included), `known_hosts` or any ssh or key
  setting. The developer owns them. Never edit them, or `lanq.yml`, to get an `--env` through.
- Do not put secrets in `--env` values.
- Do not try to get past the secret scan: it reads files git shows as binary too (0.9.2). Never add a `.gitattributes`
  entry, rename, compress, move or re-encode a file, or move a secret into a `-diff` file. Report `scanSkipped` (files
  over the scan's caps, not read in full) and a `scan-incomplete` refusal (lanq could not read a file from git); do
  not try to fix them.
- Do not write `lanq makeready` JSON `suggestedMachine` into the machines file: it is advice for the developer.
  `lanqWithheld` lists what lanq left out of the report.
- Do not edit `lanq.yml` or the scripts it runs (shard scripts included) to turn a red run green.
- Do not run `lanq makeready --add`, `lanq makeready --setup` or anything with `--yes`: adding machines is the
  developer's job.
- Do not run a `nextSteps` command whose `safeToRunUnattended` is `false`, and run a safe one at most once.
- Do not ssh to the machines yourself, and do not try to work around a refusal (never by editing `lanq.yml` or the
  machines file).
- Do not run `lanq unlock`, `lanq makeready` (also `--all`), `lanq wake` or `lanq health --wake` unless the developer asks: they act on someone's PC.
- Do not run `lanq init`: it writes a machines file, which is the developer's to create.
- Never run lanq-makeready-fix (`lanq-makeready-fix.ps1` / `.sh`), even when `lanq makeready` says a check may be fixable
  at that PC: it is the PC owner's, local and interactive only. Pass that line on to the developer.
- Do not pass permission-skipping flags such as `--dangerously-skip-permissions`; lanq rejects them.
- Do not paste whole logs into the conversation.
- Do not edit or delete lanq's `jobs.jsonl` or its `logs/` folder.
