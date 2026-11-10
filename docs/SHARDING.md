# Sharding: split long work across small machines

A test suite that takes 20 minutes on one box can take 5 on four. `lanq run --shards` starts N copies of one command
at once, each on a free slot somewhere in your pool, and tells each copy which piece it is. Your dev PC keeps
compiling and editing while the pool does the grinding.

```bash
lanq run --needs node --shards 4 -- npx jest --shard={shard}/{shards}
lanq run --needs node --shards auto -- npx playwright test --shard={shard}/{shards}
```

This page covers how the option works, how to cut each ecosystem's tests into pieces, how to size machines, and how to
route work in a pool of mixed machines. Setting up the pool itself (lanq on the orchestrator, OpenSSH on Windows and
Linux workers, `lanq init`, `lanq makeready --all`) is in [TEAM-QUICKSTART.md](TEAM-QUICKSTART.md).

## How `--shards` works

- **N copies, N jobs.** `--shards N` (1 to 64) starts N jobs of the same command. Each is a normal lanq job with its own
  job id, slot, log and cleanup. `--shards auto` sets N to the number of free slots on the machines that can take this
  job right now: the right capabilities, inside their allowed hours, enough `--mem`, and not held. It is at least 1 and
  at most 64, and lanq prints the choice and why, for example
  `[lanq] --shards auto: 5 shard(s) (free slots: tiny-01 1, big-01 4)`.
- **Preview with `--dry-run`.** The same command with `--dry-run` probes the machines once and prints the N that
  `--shards auto` would pick and where each shard would start, then runs nothing (no slot, no upload, no wake):

  ```text
  $ lanq run --needs node --shards auto --dry-run -- npx jest --shard={shard}/{shards}
  ...
  --shards auto would pick 5 (free slots: tiny-01 1, big-01 4)
  Placement now: tiny-01 shard 1; big-01 shards 2,3,4,5; 0 waiting
  ```

  With `--json` the object has `shardCount`, `shardCountAuto`, `placement` (`[{machine, shards}]`) and every machine
  as judged for the job (see [JSON.md](JSON.md#lanq-run---dry-run---json)). The placement is the one at that moment;
  a slot another run takes before the real start changes it.
- **What each shard knows.** In the command's arguments, the literal tokens `{shard}` (1-based index) and `{shards}`
  (N) are replaced by numbers. Nothing else is expanded: no shell, no other placeholders, and the result must still pass
  `allowed_commands`, the permission-flag ban, the secret scan and (for Windows workers) the unsafe-character rule, for
  **every** shard, before anything is built or sent. Each job also gets `LANQ_SHARD` and `LANQ_SHARDS` in its
  environment, next to `LANQ_JOB`, so a helper script can read them.
- **Placement.** Shards go to free slots, dealt round-robin over the eligible machines in the usual order (least
  loaded first). A machine with `max_jobs: 4` takes up to four shards at once and one with `max_jobs: 1` takes one, so
  with five shards, a 1-slot box and an equally loaded 4-slot box run one and four.
- **`--mem` is per shard.** With `--mem GB`, a machine takes no more shards at once than its free memory covers
  (free memory divided by `--mem`, rounded down), whatever its `max_jobs`: a 4-slot box with 5 GB free and `--mem 4`
  takes one shard at a time. `--shards auto` counts the same way. The free figure comes from the last probe; shards of
  this run placed there but still uploading or preparing are counted as using `--mem` each, since they have not used
  their memory yet.
- **More shards than slots.** The rest wait for a sibling shard to finish, then take the slot it freed. That wait does
  not count against `--wait`: `--wait` is how long lanq waits while no shard of this run holds a slot. When it runs
  out, the shards still waiting end `NoCapacity` (75). A machine that the probe shows free but that refuses the slot
  (another lanq took it first) sends the shard back to wait; such attempts never extend `--wait`. With `--wait` above 0 and no awake
  machine able to take a shard, lanq may wake one sleeping machine, as for a single job; `--no-wake` turns that off.
- **One snapshot.** The repository is snapshotted once, secret-scanned once, and each git bundle (full, or incremental
  on a base a worker already has) or archive is built at most once for the whole run and uploaded to every shard that
  needs it. Builds run one at a time (each git bundle build uses one temporary local ref), and a build that failed is
  tried once more by the next shard that needs it. Within one run, lanq prepares one shard at a time per machine, so
  the second shard on a machine usually gets a small incremental bundle.
- **Independent shards.** One shard failing does not stop the others. `--fail-fast` makes the first shard that does not
  complete cancel the rest (they are stopped and cleaned up, and show as `Cancelled`).
- **Retries for infrastructure only.** `--shard-retries R` (0 to 3, default 0) re-runs a shard, as a new job, only when
  its machine stopped answering ssh while the job was uploaded, prepared or running (ssh exit 255 without the job's
  own exit marker). A test failure, a timeout, a refusal or a job that itself exited 255 is never re-run. A re-run never
  risks running the same shard twice at once: when the connection dropped while the job was running and lanq's cleanup
  on that machine did not finish either, the job may still be running there, so the re-run goes to another machine that
  placement could use now (capabilities, hours, pause, memory, `--mem`, load, Windows-safe arguments); if there is none, the shard is not re-run and its message says so (`InfraError`, 70). When the
  connection dropped during upload or prepare, or the cleanup finished, the job is known not to be running and the
  re-run may use any machine.
- **Ctrl+C** cancels every shard. Each started shard's cleanup still runs (its process is stopped, its worktree and
  slot removed), and lanq exits 130.

### Output

Each shard's lines stream as they arrive, prefixed with the shard and machine and sanitised like any worker output:

```text
[2/5 big-01] [lanq] job 20261109-221503-a1b2c3 -> big-01 (slot 1)
[2/5 big-01] PASS src/cart.test.ts
[1/5 tiny-01] FAIL src/auth.test.ts
```

At the end comes a table, then the status line on stderr:

```text
[lanq] shard   machine          status        exit  duration
[lanq] 1/5     tiny-01          Failed           1     212.4s
[lanq] 2/5     big-01           Completed        0      98.0s
[lanq] 3/5     big-01           Completed        0     101.7s
[lanq] 4/5     big-01           Completed        0      95.3s
[lanq] 5/5     big-01           Completed        0      99.9s
[lanq] Failed: 4 of 5 shard(s) completed; shard 1/5: Failed: exit 1
```

### Exit code

`0` only when every shard completed. Otherwise the shard with the most telling result decides, lowest index first:

| Overall status | Exit | When |
| --- | --- | --- |
| `Completed` | 0 | every shard passed |
| `Failed` | that shard's own exit code | a shard's command failed (it wins over everything below: the code is red) |
| `Rejected` | 2 | a command line was refused (then nothing was sent at all) or a machine refused a shard's command |
| `TimedOut`, `WindowClosed` | 124 | a shard ran past `--timeout` or its machine's allowed hours |
| `InfraError` | 70 | ssh or git trouble (after `--shard-retries`, if any) |
| `NoCapacity` | 75 | some shards found no machine within `--wait` |
| `Cancelled` | 130 | Ctrl+C: every started shard was stopped and cleaned up |

### `--json`

stdout carries exactly one object. `shards` holds one entry per shard in index order, each with the fields of the
single-run object (`status`, `exitCode`, `machine`, `jobId`, `durationSeconds`, `message`, and with `--fetch` the fetch
fields) plus `shard` and `attempts`. It is empty when nothing ran (a refused command line, bad arguments or config).

```json
{"status":"Failed","exitCode":1,"shardCount":3,"durationSeconds":214.9,"message":"2 of 3 shard(s) completed; shard 1/3: Failed: exit 1",
 "shards":[{"status":"Failed","exitCode":1,"machine":"tiny-01","jobId":"20261109-221503-a1b2c3","durationSeconds":212.4,"message":"exit 1","shard":1,"attempts":1},
           {"status":"Completed","exitCode":0,"machine":"big-01","jobId":"20261109-221503-d4e5f6","durationSeconds":98.0,"message":"exit 0","shard":2,"attempts":1},
           {"status":"Completed","exitCode":0,"machine":"big-01","jobId":"20261109-221504-0a1b2c","durationSeconds":101.7,"message":"exit 0 (attempt 2)","shard":3,"attempts":2}]}
```

### Collecting results

`--fetch` works per shard: each shard's files land in their own folder, `.lanq/artifacts/<job id>/` (or
`--fetch-dir`/`<job id>/`), so shards never overwrite each other's results:

```bash
lanq run --needs dotnet,linux --shards 4 --fetch 'tests/**/TestResults/*.trx' -- sh scripts/lanq/dotnet-test-shard.sh tests/App.Tests/App.Tests.csproj
```

## Splitting the work, by ecosystem

Every command below must be in `policy.allowed_commands` (an entry matches the leading words, for example
`"npx jest"` or `"sh scripts/lanq/dotnet-test-shard.sh"`). A shard always runs one command in the job's working folder;
anything more (build then test, choose a list of files) goes in a small script you commit. The script then runs with
the job's own rights, like any allowlisted command, so review it like code.

Windows workers refuse arguments containing `"` `&` `|` `<` `>` `^` `%`. Keep such characters (a test filter with `|`,
for example) inside the script, not on the lanq command line. The same command runs on every shard's machine, so in a
pool of Linux and Windows workers either use a command that works on both or keep the shards on one kind with a
capability tag (`--needs linux`).

### .NET

`dotnet test` has no built-in sharding. Split by test class: list the tests, take every Nth class, run those with
`--filter`. Build once inside the job and test with `--no-build`, so listing and running do not each build again.
(Every shard still builds once on its own worker: the worktree is fresh. On small boxes the build can be most of a
shard's time, so prefer fewer, larger shards there, or keep the build local and send only test projects that build
fast.)

`scripts/lanq/dotnet-test-shard.sh` (Linux workers):

```sh
#!/bin/sh
set -eu
: "${LANQ_SHARD:?run this through lanq run --shards}"
: "${LANQ_SHARDS:?run this through lanq run --shards}"
PROJECT=${1:?usage: dotnet-test-shard.sh path/to/Tests.csproj}
dotnet build "$PROJECT" -c Release -v q -nologo
classes=$(dotnet test "$PROJECT" -c Release --no-build --list-tests -nologo \
  | sed -n 's/^    //p' | sed 's/(.*//' | sed 's/\.[^.]*$//' | sort -u \
  | awk -v n="$LANQ_SHARDS" -v i="$LANQ_SHARD" '(NR - 1) % n == i - 1')
[ -n "$classes" ] || { echo "shard $LANQ_SHARD/$LANQ_SHARDS: no test classes"; exit 0; }
filter=$(printf '%s\n' "$classes" | sed 's/.*/FullyQualifiedName~&./' | paste -sd'|' -)
echo "shard $LANQ_SHARD/$LANQ_SHARDS: $(printf '%s\n' "$classes" | wc -l | tr -d ' ') test classes"
exec dotnet test "$PROJECT" -c Release --no-build --filter "$filter" -nologo --logger "trx;LogFileName=shard-$LANQ_SHARD.trx"
```

`scripts/lanq/dotnet-test-shard.ps1` (Windows workers; the `-ExecutionPolicy Bypass` on the command line is needed
because the default policy on Windows 10 refuses scripts):

```powershell
param([Parameter(Mandatory)][string]$Project)
$ErrorActionPreference = 'Stop'
if (-not $env:LANQ_SHARD -or -not $env:LANQ_SHARDS) { throw 'run this through lanq run --shards' }
$i = [int]$env:LANQ_SHARD; $n = [int]$env:LANQ_SHARDS
dotnet build $Project -c Release -v q -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$names = dotnet test $Project -c Release --no-build --list-tests -nologo | Where-Object { $_ -like '    *' }
$classes = @($names | ForEach-Object { ($_.Trim() -replace '\(.*$', '') -replace '\.[^.]*$', '' } | Sort-Object -Unique)
$mine = @(for ($k = 0; $k -lt $classes.Count; $k++) { if ($k % $n -eq $i - 1) { $classes[$k] } })
if ($mine.Count -eq 0) { "shard $i/$n`: no test classes"; exit 0 }
$filter = ($mine | ForEach-Object { "FullyQualifiedName~$_." }) -join '|'
"shard $i/$n`: $($mine.Count) test classes"
dotnet test $Project -c Release --no-build --filter $filter -nologo --logger "trx;LogFileName=shard-$i.trx"
exit $LASTEXITCODE
```

```bash
lanq run --needs dotnet,linux --shards auto --fetch 'tests/**/TestResults/*.trx' -- sh scripts/lanq/dotnet-test-shard.sh tests/App.Tests/App.Tests.csproj
lanq run --needs dotnet,windows --shards 2 -- powershell -NoProfile -ExecutionPolicy Bypass -File scripts/lanq/dotnet-test-shard.ps1 -Project tests/App.Tests/App.Tests.csproj
```

Both scripts were checked against an xUnit project with VSTest (`dotnet test --list-tests` prints one fully qualified
test name per line, indented four spaces; parameters in parentheses are dropped). Classes are split by position in a
sorted list, so the split is stable while the set of classes does not change. If one class dominates, split by
namespace buckets you choose instead (a `case "$LANQ_SHARD" in 1) filter=...` table in the script). With Microsoft
Testing Platform projects, check `--list-tests` output on your project before relying on the `sed`.

### Node: Jest, Vitest, Playwright

All three shard natively with `--shard=i/N`:

```bash
lanq run --needs node --shards 4 -- npx jest --shard={shard}/{shards}
lanq run --needs node --shards 4 -- npx vitest run --shard={shard}/{shards}
lanq run --needs node --shards 4 -- npx playwright test --shard={shard}/{shards}
```

`--shard` was added in Jest 28; a custom `testSequencer` must implement a `shard` method. Vitest's `--shard` works with
`vitest run` (not watch mode). Each shard needs `node_modules` on the worker; a committed lockfile plus an install step in a
script (`npm ci` then the test command) is the simple way, and on small boxes `NODE_OPTIONS=--max-old-space-size=...`
under the machine's `env:` keeps a shard from swapping (see "Old machines with little RAM" in the README).

### Python

With [pytest-split](https://pypi.org/project/pytest-split/) installed on the workers (its `--group` is 1-based):

```bash
lanq run --needs python --shards 4 -- python -m pytest --splits {shards} --group {shard}
```

pytest-split balances by recorded durations when a `.test_durations` file is committed, and otherwise splits by count.
Without the plugin, a script can split the file list:

```sh
#!/bin/sh
set -eu
files=$(find tests -name 'test_*.py' | sort | awk -v n="$LANQ_SHARDS" -v i="$LANQ_SHARD" '(NR - 1) % n == i - 1')
[ -n "$files" ] || exit 0
exec python -m pytest $files
```

(`-k` expressions work too, but they select by name, so a split by file is easier to keep complete.)

### Go

```sh
#!/bin/sh
set -eu
pkgs=$(go list ./... | awk -v n="$LANQ_SHARDS" -v i="$LANQ_SHARD" '(NR - 1) % n == i - 1')
[ -n "$pkgs" ] || exit 0
exec go test $pkgs
```

```bash
lanq run --needs go --shards 3 -- sh scripts/lanq/go-test-shard.sh
```

### Rust

[cargo-nextest](https://nexte.st/) partitions natively (`slice:m/n`, 1-based; the older `count:m/n` is deprecated,
and `hash:m/n` keeps a test in the same bucket as tests are added):

```bash
lanq run --needs rust --shards 4 -- cargo nextest run --partition slice:{shard}/{shards}
```

Each shard compiles the test binaries on its worker; on small boxes `CARGO_BUILD_JOBS` under the machine's `env:`
keeps the compile from running out of memory.

## What to keep local and what to send

Keep on the dev box: editing, compiling while you work, a single test you are iterating on, anything interactive.
The round trip (snapshot, upload, prepare) costs seconds, which only pays off for work that takes minutes.

Send to the pool: test suites in shards, lint and type-check (each can be its own job, in parallel with the tests),
slow integration suites, and container builds on workers that have Docker (`--needs docker`).

## Sizing machines

Give each machine a `max_jobs` and a `min_free_mem_gb` that fit it. These are the rules `lanq-makeready` uses for its
suggested `machines.yml` entry:

| Total RAM | Suggested `max_jobs` | Suggested `min_free_mem_gb` |
| --- | --- | --- |
| under 6 GB | 1 | 1.5 |
| 6 to under 12 GB | 2 | 2 |
| 12 to under 24 GB | 3, or 4 with 8 or more logical CPUs | 2 |
| 24 GB or more | 6, or 8 with 16 or more logical CPUs | 2 |

`max_jobs` is never above the machine's logical CPU count. Why: a test shard or build (a .NET or Node test host, a
compiler, the build server) usually needs 1.5 to 3 GB and about a core or two, and the operating system and whoever
uses the PC need the rest; more jobs than cores only makes every job slower.

`max_jobs` is the hard limit on shards running at once on a machine. `min_free_mem_gb` is the headroom kept free
before the next job starts, not an amount per job (it is not multiplied by `max_jobs`): when available memory is
below it (or, on Linux, `policy.max_mem_pressure` is exceeded), the machine is held and takes no new work, including
when the owner's own programs use the memory. It is checked when work is placed, so it cannot stop shards placed in
the same moment; that is why `max_jobs` must fit the RAM.

Keep a shard under a few minutes: a short shard loses little when a machine goes away or its hours end, and spreads
evenly. On a machine under 6 GB, run one shard at a time and cap tool memory with `env:` (`NODE_OPTIONS`,
`DOTNET_GCHeapHardLimitPercent`, `CARGO_BUILD_JOBS`), and for .NET pass `-m:1` to `dotnet build` and `dotnet test`
(one MSBuild node). [TEAM-QUICKSTART.md, Sizing micro PCs](TEAM-QUICKSTART.md#8-sizing-micro-pcs) has the rest of the
small-PC settings (heap caps, transfer mode, disk).

`lanq health` shows each machine's cores, total RAM, free memory, free/total slots and what it suits, from the
reported total memory, cores and capabilities, by the same rule and in the same words as `lanq-makeready`, so a PC gets
the same tier from both. The OS keeps some memory back, so the reported memory is first rounded **up** to whole GB (a
16 GB PC that reports 15.8 GB counts as 16, one that reports 5.8 GB as 6); then **light** under 6 GB (lint and unit-test
shards), **medium** 6 to under 16 GB (test shards and builds), **heavy** 16 GB or more with at least 4 cores (full
suites and large builds; a big machine with fewer cores is medium), and "+ container builds" when the machine has a
`docker` capability. `lanq health --json` has the same as `cores`, `memTotalGb`, `freeSlots`, `size` and
`suitedFor`.

## Routing work in a mixed pool

Three patterns cover most of it:

- **A full suite on a big box.** `--mem GB` asks for a machine with at least that much memory **available now** (not
  installed): `lanq run --mem 8 -- dotnet test` goes only to a machine reporting 8 GB or more free. A 16 GB machine
  rarely has 16 GB free, so `--mem 16` would usually find nothing; ask for what the job needs. With `--shards`, the
  figure is per shard (see above).
- **Slices across everything.** `lanq run --shards auto -- ...` uses every free slot, so small boxes take their one
  shard each and big ones take several.
- **Tags for what you know.** Capabilities are free-form tags. Add your own, such as `bigmem` on machines with 16 GB or
  more, and ask for them: `lanq run --needs dotnet,bigmem -- dotnet test`. `--needs` requires every tag listed.

### Worked example: 2 dev boxes and 4 micro PCs

The team has two developer PCs (32 GB, 16 cores, Windows 10 or Linux, Docker installed) and four micro PCs (4 GB,
4 cores, Linux). The dev boxes lend half their capacity to the pool and stay usable:

```yaml
machines:
  - name: micro-01            # micro-02 .. micro-04 the same
    os: linux
    ssh: build@10.20.30.21
    work_root: /srv/lq
    capabilities: [dotnet, node, linux]
    max_jobs: 1
    min_free_mem_gb: 1.5
    env:
      DOTNET_GCHeapHardLimitPercent: "0x32"
      NODE_OPTIONS: "--max-old-space-size=2048"
  - name: dev-a               # dev-b the same
    os: linux
    ssh: build@10.20.30.11
    work_root: /srv/lq
    capabilities: [dotnet, node, linux, docker, bigmem]
    max_jobs: 4               # the sizing rule gives 8 (32 GB, 16 CPUs); lending 4 leaves the developer room
    min_free_mem_gb: 2        # jobs run at low priority by default
```

What runs where:

| Work | Command | Lands on |
| --- | --- | --- |
| Editing, compiling, one test you are fixing | (local) | your own dev box |
| Unit tests, every push | `lanq run --needs node --shards auto -- npx jest --shard={shard}/{shards}` | up to 12 slots: one per micro, four per dev box |
| Lint and type-check, alongside | `lanq run --needs node -- npx eslint .` and `lanq run --needs node -- npx tsc --noEmit` | whichever slot is free, often a micro |
| Full integration suite | `lanq run --needs dotnet,bigmem --mem 6 -- dotnet test tests/Integration` | dev-a or dev-b |
| Container build | `lanq run --needs docker -- docker build -t app:test .` | dev-a or dev-b |

The micro PCs do most of the slicing; the big boxes take the heavy jobs and the overflow. Old or cheap machines that
would otherwise sit in a drawer add real test capacity, so the team buys fewer new machines and less RAM.

## For AI coding agents

1. Run `lanq health --json` first. Read `state`, `freeSlots`, `size` and `suitedFor` for each machine.
2. Choose: one heavy job (full suite, large build, container build) goes as a single run to a machine that suits it
   (`--needs bigmem` if the developer tags machines that way, or `--mem`). A long suite that splits cleanly goes as
   `--shards auto`, or `--shards N` with N at most the sum of `freeSlots` on machines that can take it.
   `lanq health --needs <caps> --json` gives each machine's `jobSlots` for the job, and the run with `--dry-run`
   shows N and the placement without running anything.
3. Only shard commands that are safe to run several times at once: tests, lint, checks. Never shard a command that is
   not idempotent or that writes shared state (deploys, migrations against a shared database, publishing, anything that
   pushes).
4. Read the `--json` result: overall `status` and `exitCode`, then each entry in `shards`. Report failing shards by
   index and machine, and their failing tests from the log.
5. Use `--shard-retries 1` at most, and only for lost machines: lanq never retries a test failure, and neither should
   you. Do not re-run a whole sharded run in a loop.
6. Keep `--no-wake` unless the developer has said waking machines is fine.

## Flaky and order-dependent tests

Sharding changes which tests run together and in what order, so it finds hidden coupling: tests that depend on a
previous test's state, on a shared port, file or database, or on running alone. A test that passes alone and fails in
a shard is usually one of those; fix the coupling (unique temp folders and ports, a fresh database per test class)
rather than pinning shard membership. Shards on one machine run at the same time in separate worktrees, so tests that
use fixed ports or fixed paths outside the worktree collide; give such suites `max_jobs: 1` machines or a capability tag.
A failure that comes and goes is flaky whether sharded or not: re-run that shard's tests locally, do not raise
`--shard-retries` (it only covers lost machines).
