# Sharding: split long work across small machines

A test suite that takes 20 minutes on one box can take 5 on four. `lanq run --shards` starts N copies of one command
at once, each on a free slot somewhere in your pool, and tells each copy which piece it is. Your dev PC keeps
compiling and editing while the pool does the grinding.

```bash
lanq run --needs node --shards 4 -- npx jest --shard={shard}/{shards}
lanq run --needs node --shards auto -- npx playwright test --shard={shard}/{shards}
```

This page covers how the option works, how to pick N from earlier runs (`--target-minutes`), how to cut each
ecosystem's tests into pieces and balance them by duration, how to size machines, and how to route work in a pool of
mixed machines. Setting up the pool itself (lanq on the orchestrator, OpenSSH on Windows and
Linux workers, `lanq init`, `lanq makeready --all`) is in [TEAM-QUICKSTART.md](TEAM-QUICKSTART.md).

## How `--shards` works

- **N copies, N jobs.** `--shards N` (1 to 64) starts N jobs of the same command. Each is a normal lanq job with its own
  job id, slot, log and cleanup. `--shards auto` sets N to the number of free slots on the machines that can take this
  job right now: the right capabilities, inside their allowed hours, enough `--mem`, and not held. It is at least 1 and
  at most 64, and lanq prints the choice and why, for example
  `[lanq] --shards auto: 5 shard(s) (free slots: tiny-01 1, big-01 4)`. With `--target-minutes M`, lanq instead
  picks N from earlier runs of the same command, so each shard takes about M minutes (see
  [Choosing N from history](#choosing-n-from-history---target-minutes)).
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

## Choosing N from history: --target-minutes

Plain `--shards auto` uses every free slot. That is fast for one person, but it takes the whole pool for a suite that
would finish nearly as soon on fewer machines. `--shards auto --target-minutes M` (M from 1 to 1440) asks a different
question: how many shards does this suite need so that each one takes about M minutes?

```bash
lanq run --needs node --shards auto --target-minutes 10 -- npx jest --shard={shard}/{shards}
```

`--target-minutes` works only with `--shards auto`; with `--shards N` or without `--shards` the command line is
refused (exit 2). Plain `--shards auto` is unchanged and never reads the job log.

### The rule

1. **History.** lanq reads `jobs.jsonl` (and `jobs.1.jsonl`) on this computer. It keeps the jobs of the same command
   (the same arguments, with `{shard}` and `{shards}` as written, and the same `--dir`; `--needs` and `--mem` do not
   matter) that completed, have a recorded run time and started in the last 90 days. The shards of one run count as
   one run, and a retried shard counts once. A run counts when at least half of its shards completed. lanq uses the
   newest 10 such runs.
2. **Work and overhead.** For each run, the work is the sum of its shards' own run times when every shard completed,
   else N times the median shard. Each shard's run time is first divided by its machine's speed factor (below), so
   the work is "on a typical machine". The overhead is the median, over the run's shards, of wall-clock minus run
   time: waiting for the slot, upload, prepare, fetch and cleanup. W and O are the medians over the runs (the plain
   median: the mean of the two middle values for an even count).
3. **Speed factors.** Every completed job of the last 90 days that ran 30 s or more (any command) is a sample: its run
   time times its N, divided by the median of that over its command. A machine with 5 or more samples gets the median
   of its own, clamped to 0.25 to 4 and rounded to two decimals; 1.62 means 62% slower than typical. A machine with
   fewer borrows the median factor of the measured machines of its size class (light, medium or heavy, from this
   probe); else it counts as 1. `lanq stats` shows each machine's factor.
4. **Pick.** The budget per shard is 60 x M - O seconds. N0 = ceil(W / budget). lanq deals N0 shards over the free
   slots as a run would and takes the largest speed factor f among those machines, then N = ceil(f x W / budget). When
   that N is larger, it is dealt once more and f taken again (two passes at most, so the choice is stable). The
   slowest machine a shard would go to decides.
5. **Cap.** N is never more than the free slots that can take the job now, nor 64. With no free slot N is 1.
6. **Overhead.** When the overhead leaves less than a tenth of M for the work, more shards cannot reach M: lanq uses
   every free slot.

Each shard should then take about (O + f x W / N) / 60 minutes on the slowest machine. lanq prints the reasoning on
the `--shards auto` line:

```text
[lanq] --shards auto: 4 shard(s) (--target-minutes 5: 3 earlier runs, about 10.7 min of work + 30 s per shard, slowest machine tiny-01 x1.62: about 4.8 min each; free slots: big-01 4, tiny-01 1)
```

The slowest machine is named only when its speed is known (measured or from its size class).

### Preview with --dry-run

```text
$ lanq run --needs node --shards auto --target-minutes 5 --dry-run -- npx jest --shard={shard}/{shards}
...
--shards auto would pick 4 (free slots: big-01 4, tiny-01 1)
Timing:   --target-minutes 5: 3 earlier runs, about 10.7 min of work + 30 s per shard, slowest machine tiny-01 x1.62: about 4.8 min each
Placement now: big-01 shards 1,3,4; tiny-01 shard 2; 0 waiting
```

With `--json` the plan has `shardCountWhy` and a `shardTiming` object (`basis`, `runsUsed`, `workMinutes`,
`overheadSeconds`, `slowestMachine`, `speedFactor`, `wanted`, `freeSlots`, `capped`, `picked`,
`expectedShardMinutes`, `why`); see [JSON.md](JSON.md#shardtiming). `lanq run --shards auto --target-minutes M --json`
carries the same `shardTiming`. A dry run reads the job log and never writes it, and it picks the same N as a run
would from the same history and probe.

The other `Timing:` lines you may see:

```text
Timing:   --target-minutes 5: no completed run of this command in the last 90 days; --shards auto falls back to the free slots
Timing:   --target-minutes 3: 6 earlier runs, about 16.8 min of work + 41 s per shard; 8 shards would be needed for 3 min; capped at 5 free slots: about 4.0 min each
Timing:   --target-minutes 5: 6 earlier runs, about 16.8 min of work + 6.2 min per shard; the per-shard overhead (6.2 min) is above --target-minutes 5; more shards cannot reach it, so all 5 free slots: about 9.6 min each
```

### No history, and history from a different N

- **No history.** Timing history starts with 0.7.0: older `jobs.jsonl` lines have no command key or run time. The
  first run of a command (or the first after 90 days) uses the free slots, as plain `--shards auto`, and says so. From
  the next run on, lanq has a run to learn from. `lanq stats` shows `timing: none yet` until then.
- **A different N.** The work is the whole suite, not one shard, so runs with any N count: 4 shards of 4 minutes and
  8 shards of 2 minutes are both 16 minutes of work. A single run (`--shards` not used) of the same command counts
  too, as a run of 1 shard; a command with `{shard}` in it is a different command from one without.
- **A changed command.** Other arguments or another `--dir` are another command, with its own history. Failed and
  timed-out runs never count, and one cold run (a fresh cache on a worker) moves the medians little.

### When plain auto is better

- You want the result as soon as possible and nobody else needs the pool now: plain `--shards auto` takes every slot.
- The suite changes a lot between runs (tests added or removed in bulk), so the history no longer fits.
- The test runner splits unevenly: lanq can only pick N. If one shard always takes twice as long as the others, fix
  the split first (see the recipes below), or every estimate is off by that shard.
- The overhead is most of a shard's time (a long build in every shard): `--target-minutes` then uses every free slot
  anyway, and fewer, larger shards may be better (`--shards N`).

`lanq stats` (and `lanq stats --json`) shows each command's `timing` (the work and per-shard overhead lanq would use),
and `lanq stats --target-minutes M` adds the shards M needs on a typical machine, without probing anything.

## Splitting the work, by ecosystem

Every command below must be in `policy.allowed_commands` (an entry matches the leading words, for example
`"npx jest"` or `"sh scripts/lanq/dotnet-test-shard.sh"`). A shard always runs one command in the job's working folder;
anything more (build then test, choose a list of files) goes in a small script you commit. The script then runs with
the job's own rights, like any allowlisted command, so review it like code.

Windows workers refuse arguments containing `"` `<` `>` `^` `%`, and `&` `|` unless the program is a native `.exe`
(`dotnet test --filter "A|B"` is fine; `npm run x -- "a|b"` is not, since `npm.cmd` re-parses it through cmd.exe). Keep
such characters inside the script, not on the lanq command line. The same command runs on every shard's machine, so in a
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

## Balancing shards: recipes

lanq picks N. The test runner decides which tests go in which shard. Splitting by count or by position (as above)
is fine while tests take about the same time; when a few slow files or classes land together, one shard runs long
and the whole run waits for it. These recipes split by recorded durations instead. Each is a sketch: check the
runner's own docs for its exact options and version. Every command must still be in `policy.allowed_commands`.

### pytest: pytest-split with committed durations

[pytest-split](https://github.com/jerry-git/pytest-split) balances by the durations in a `.test_durations` file at the
repository root. Record them on a worker once in a while and commit the file:

```bash
lanq run --needs python --fetch .test_durations -- python -m pytest --store-durations
```

The file comes back in `.lanq/artifacts/<job id>/.test_durations`; copy it to the repository root and commit it. Then
shard as usual (`--splits {shards} --group {shard}`). New tests that are not in the file still run; refresh the file
when the suite has changed a lot. See pytest-split's README for `--durations-path` and its splitting algorithms.

### Jest: a testSequencer with a shard() method

Jest 28 and later call a custom `testSequencer`'s `shard(tests, {shardIndex, shardCount})` for `--shard`. Read a
durations file (seconds per test file, relative to the repository root) and deal the files, longest first, to the
shard with the least time so far. Every shard computes the same split, so it must be deterministic:

```js
// durations-sequencer.js; in jest.config.js: testSequencer: './durations-sequencer.js'
const Sequencer = require('@jest/test-sequencer').default;
const path = require('path');
const fs = require('fs');

const durations = fs.existsSync('test-durations.json') ? JSON.parse(fs.readFileSync('test-durations.json', 'utf8')) : {};

class DurationsSequencer extends Sequencer {
  shard(tests, { shardIndex, shardCount }) {
    const key = (t) => path.relative(process.cwd(), t.path).split(path.sep).join('/');
    const time = (t) => durations[key(t)] ?? 10; // a new file counts as 10 s
    const sorted = [...tests].sort((a, b) => time(b) - time(a) || (key(a) < key(b) ? -1 : 1));
    const load = new Array(shardCount).fill(0);
    const mine = [];
    for (const t of sorted) {
      let best = 0;
      for (let s = 1; s < shardCount; s++) if (load[s] < load[best]) best = s;
      load[best] += time(t);
      if (best === shardIndex - 1) mine.push(t);
    }
    return mine;
  }
}

module.exports = DurationsSequencer;
```

Build `test-durations.json` (`{"src/cart.test.ts": 42.5, ...}`) from the per-file times of a full run, for example
the output of `npx jest --json --outputFile=jest-results.json` fetched back with `--fetch jest-results.json`, and
commit it. See Jest's `testSequencer` docs.

### Playwright: --shard with fullyParallel

Playwright shards whole test files by default. With `fullyParallel: true` in `playwright.config` it shards single
tests, which evens the shards out without any durations file:

```bash
lanq run --needs node --shards auto --target-minutes 10 --fetch 'blob-report/**' -- npx playwright test --shard={shard}/{shards} --reporter=blob
```

Each shard's blob report comes back in its own `.lanq/artifacts/<job id>/blob-report/`. Copy the files into one folder
and run `npx playwright merge-reports --reporter html <folder>` locally. See Playwright's sharding docs.

### .NET: a filter per shard from earlier trx durations

`dotnet test` has no durations-based split, so a script does it. On the dev box, turn earlier `.trx` files (fetched
with `--fetch 'tests/**/TestResults/*.trx'`) into one line per test class, `Namespace.Class seconds`, and commit it:

```powershell
# scripts/lanq/make-test-durations.ps1 <folder with .trx files>: writes test-durations.txt
$t = @{}
foreach ($f in Get-ChildItem -Recurse -Filter *.trx $args[0]) {
  $x = [xml](Get-Content -Raw $f.FullName)
  $class = @{}
  foreach ($u in $x.TestRun.TestDefinitions.UnitTest) { $class[$u.id] = $u.TestMethod.className.Split(',')[0] }
  foreach ($r in $x.TestRun.Results.UnitTestResult) {
    if ($class[$r.testId] -and $r.duration) { $t[$class[$r.testId]] += [TimeSpan]::Parse($r.duration).TotalSeconds }
  }
}
$t.Keys | Sort-Object | ForEach-Object { [string]::Format([cultureinfo]::InvariantCulture, '{0} {1:0.0}', $_, $t[$_]) } | Set-Content test-durations.txt
```

A small helper deals names to shards by those durations, longest first, each to the shard with the least time so
far (the same for every shard):

```sh
#!/bin/sh
# scripts/lanq/shard-by-durations.sh DURATIONS: reads names on stdin, prints this shard's ($LANQ_SHARD of $LANQ_SHARDS).
# A name missing from DURATIONS (a new class or package) counts as 10 s.
set -eu
awk -v n="$LANQ_SHARDS" -v i="$LANQ_SHARD" '
  FILENAME == ARGV[1] { d[$1] = $2; next }
  { k++; name[k] = $1; t[k] = ($1 in d) ? d[$1] + 0 : 10 }
  END {
    for (a = 1; a <= k; a++) for (b = a + 1; b <= k; b++)
      if (t[b] > t[a] || (t[b] == t[a] && name[b] < name[a])) { x = t[a]; t[a] = t[b]; t[b] = x; y = name[a]; name[a] = name[b]; name[b] = y }
    for (a = 1; a <= k; a++) {
      best = 1; for (s = 2; s <= n; s++) if (load[s] < load[best]) best = s
      load[best] += t[a]; if (best == i) print name[a]
    }
  }' "$1" -
```

In `dotnet-test-shard.sh` above, replace the `awk` that takes every Nth class with
`sh scripts/lanq/shard-by-durations.sh test-durations.txt`. A long class list makes a long `--filter`: Windows caps a
command line at 32,767 characters and cmd.exe at 8,191, so write the filter into a `.runsettings` file and pass that
instead (`TreatNoTestsAsError` makes a shard that ran no tests fail):

```sh
filter=$(printf '%s\n' "$classes" | sed 's/.*/FullyQualifiedName~&./' | paste -sd'|' -)
printf '<RunSettings><RunConfiguration><TestCaseFilter>%s</TestCaseFilter><TreatNoTestsAsError>true</TreatNoTestsAsError></RunConfiguration></RunSettings>\n' "$filter" > shard.runsettings
exec dotnet test "$PROJECT" -c Release --no-build --settings shard.runsettings -nologo --logger "trx;LogFileName=shard-$LANQ_SHARD.trx"
```

A `&` in a filter must be written `&amp;` in the file. See Microsoft's ".runsettings" docs for `TestCaseFilter`.

### Go: split packages by go test -json durations

`go test -json` reports each package's time in its `pass` or `fail` event (the one without a `Test` field). Record it
on the dev box (or through lanq with a script and `--fetch`), and commit the result:

```bash
go test -json ./... | jq -r 'select(.Test == null and (.Action == "pass" or .Action == "fail") and .Elapsed != null) | "\(.Package) \(.Elapsed)"' > go-durations.txt
```

Then the shard script deals packages with the same helper:

```sh
#!/bin/sh
set -eu
pkgs=$(go list ./... | sh scripts/lanq/shard-by-durations.sh go-durations.txt)
[ -n "$pkgs" ] || { echo "shard $LANQ_SHARD/$LANQ_SHARDS: nothing to run"; exit 0; }
exec go test $pkgs
```

### Two more rules

- **Give each shard its own services.** Shards run at the same time, and several can share one machine. A test
  database or broker on a fixed port, shared by all shards, makes them collide. Start one container per shard on a
  random free port (for example with Testcontainers) and read the port back, or give each shard its own database
  name from `LANQ_SHARD`.
- **Treat "no tests ran" as a failure.** A wrong filter or a renamed folder can make a shard pass with no tests at
  all. Make that fail: `TreatNoTestsAsError` for .NET, pytest's own exit 5 when it collects nothing, and no
  `--passWithNoTests` for Jest. The one exception is a shard that is empty on purpose because there are more shards
  than pieces (the scripts above print a line and exit 0 then); keep N below the number of pieces.

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
   shows N and the placement without running anything. Once the command has history (`lanq stats --json` shows a
   `timing` for it), prefer `--shards auto --target-minutes 10`, previewed with `--dry-run`.
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
