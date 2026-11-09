# JSON output

`--json` is how scripts and coding agents should read `lanq`. This page is the contract: what each command prints,
which keys it has, and what each status and exit code means.

These commands take `--json`: `lanq run` (also with `--shards`), `lanq health`, `lanq makeready` (one machine, `--ssh`
or `--all`), `lanq init` and `lanq wake`. `lanq doctor`, `lanq stats` and `lanq unlock` have no JSON output.

## Guarantees

- **One object on stdout.** With `--json`, stdout carries exactly one JSON value and nothing else: an object, or for
  `lanq health` an array. That holds on the paths where nothing normal happened too: a bad argument, a config error,
  a refusal, Ctrl+C or an unexpected error. You get a small object with `status`, `exitCode` and `message` instead.
- **Everything else goes to stderr.** The job's own output, the `[lanq] ...` lines, warnings and error messages all go
  to stderr. Redirect stderr to a file and stdout stays clean to parse.
- **One line, except one report.** Every object is printed on one line, except the single-machine `lanq makeready
  --json` report, which is indented over several lines. Read the whole of stdout and parse it; do not parse line by line.
- **Key casing.** lanq's own keys are camelCase (`exitCode`, `durationSeconds`, `diskFreeGb`). The makeready report
  is the lanq-makeready script's own schema and keeps its keys: `verdicts` uses `worker_git`, `worker_archive`,
  `dotnet10`, `docker` and `overnight`, and `suggestedMachine` uses `work_root`, `max_jobs`, `min_free_mem_gb` and
  `allowed_hours`, as in `machines.yml`.
- **Null versus absent.** In lanq's own objects a key that is listed below is always there; when it has no value it is
  `null` (for example `machine` when no machine took the job). The exceptions are marked "conditional": they are left
  out entirely when they do not apply (`shard`, `attempts` and the `--fetch` keys on a run, `problem` and `report` in
  `makeready --all`). Inside a makeready report, keys without a value are left out, never `null`.
- **Strings are sanitised.** Messages and anything that came from a worker have control, bidi and zero-width
  characters replaced by `?`. The serializer may also escape characters such as `<`, `>`, `&` and `'` as `<` and
  so on, so read strings through a JSON parser, not with grep.
- **Compatibility.** The intent is that fields may be added in minor releases, and that existing fields are not removed
  or retyped without a note in [CHANGELOG.md](../CHANGELOG.md). Ignore keys you do not know.

If lanq is killed outright (not Ctrl+C), stdout may be empty. Treat empty or unparsable stdout as an infrastructure
error and do not guess the result.

## Status and exit codes

| Command | `status` | Exit | Meaning |
| --- | --- | --- | --- |
| `run` | `Completed` | 0 | The job passed. |
| `run` | `Failed` | the job's own code | The job ran and exited non-zero. |
| `run` | `Rejected` | 2 | Bad arguments, a config error, or refused: not on the allowlist, a permission-skipping flag, a secret in the payload or command line, or a command the machine cannot run. Nothing ran. |
| `run` | `NoCapacity` | 75 | No eligible machine within `--wait`, or the machine's allowed hours were about to end after upload. |
| `run` | `InfraError` | 70 | ssh, git or snapshot trouble, a lost connection, or an unexpected error. |
| `run` | `TimedOut` | 124 | The job ran past `--timeout`. |
| `run` | `WindowClosed` | 124 | The machine's allowed hours (plus grace) ended while the job ran. |
| `run` | `Cancelled` | 130 | Ctrl+C. The job was cleaned up where it had started. |
| `run --shards` | any of the above | as above | Overall result; see [lanq run --shards](#lanq-run---shards---json). |
| `health` | (an array) | 0 | Every machine was probed. Machines that are down or held are in the array; that is not an error. |
| `makeready <machine>` | (the report) | 0 or 1 | 0 worker-ready, 1 not ready. The report has no `status` key. |
| `makeready --all` | `Ok` | 0 | Every machine is ready (warnings alone still exit 0). |
| `makeready --all` | `Failing` | 1 | At least one machine is NOT READY. |
| `makeready --all` | `InfraError` | 70 | A machine was unreachable or sent an unusable report. |
| `makeready --all` | `Held` | 75 | A machine is paused or outside its allowed hours (and none of the above). |
| `init` | `Created` | 0 | The starter `machines.yml` was written. |
| `init` | `Rejected` | 2 | The path exists (file, folder or link) or bad arguments. |
| `init` | `InfraError` | 70 | The file could not be written. |
| `wake` | `Sent` | 0 | The magic packet was sent (no `--wait`). |
| `wake` | `Awake` | 0 | Sent, and the machine answered ssh (`--wait`). |
| `wake` | `NoAnswer` | 1 | Sent, but no ssh answer within `wake.wait_seconds` (`--wait`). |
| error object | `Rejected` | 2 | `health`, `makeready`, `wake`: bad arguments or config; `wake`: unknown machine or no `wake:` setting. |
| error object | `InfraError` | 70 | `health`, `makeready`, `wake`: an unexpected error; `wake`: the packet could not be sent; `makeready <machine>`: ssh failed or the report was unusable. |
| error object | `Held` | 75 | `makeready <machine>` only: the machine is paused or outside its allowed hours (`--force` scans anyway). |
| error object | `Cancelled` | 130 | Ctrl+C. |

Several exit codes cover more than one case, so read `status`, not only the exit code:

- **124** is `TimedOut` or `WindowClosed`.
- **75** is `NoCapacity` (run), `Held` (makeready) or `Held` (makeready --all).
- **70** is `InfraError` everywhere; for `makeready --all` the full object has `machines` and the error object does not.
- **1** is a failed job, a NOT READY machine, or `NoAnswer` from `wake`.
- A job's own exit code can be any number, including 2, 70, 75, 124 or 130. `Failed` with exit 2 is the job's own
  failure, not a refusal.

## lanq run --json

The job's output streams to stderr. When the run is over, stdout gets one object:

```json
{"status":"Completed","exitCode":0,"machine":"build-01","jobId":"20261109-221503-a1b2c3","durationSeconds":84.2,"message":"exit 0"}
```

| Key | Type | Notes |
| --- | --- | --- |
| `status` | string | `Completed`, `Failed`, `Rejected`, `NoCapacity`, `InfraError`, `TimedOut`, `WindowClosed` or `Cancelled`. |
| `exitCode` | number | lanq's own exit code, the same as the process exit code. |
| `machine` | string or null | The machine that took the job; `null` when none did. |
| `jobId` | string or null | `null` when lanq stopped before it made one (bad arguments, config error, Ctrl+C, unexpected error). A refusal by policy or `NoCapacity` still has one. |
| `durationSeconds` | number | Wall-clock seconds for the whole run, one decimal. |
| `message` | string | One line: `exit 1`, `timed out after 30 min`, the refusal or the error. |

A refused command line:

```json
{"status":"Rejected","exitCode":2,"machine":null,"jobId":null,"durationSeconds":0,"message":"lanq run does not take --force."}
```

### With --fetch (conditional)

When the command line has `--fetch`, the object always has `fetched` and `fetchSkipped`, possibly empty, on every
path, a refused or cancelled run included. The other fetch keys appear only when they have a value. Without `--fetch`
none of these keys appear.

| Key | Type | When |
| --- | --- | --- |
| `fetched` | array of `{path, bytes, sha256}` | Always with `--fetch`. `path` is relative to the job's folder under `fetchDir`; `sha256` is lowercase hex. |
| `fetchSkipped` | array of `{path, reason}` | Always with `--fetch`. At most 200 entries; `reason` is a short sentence. |
| `fetchSkippedMore` | number | Only when more files were skipped than `fetchSkipped` lists. |
| `fetchDir` | string | Only when a fetch ran: the local folder, `.lanq/artifacts/<jobId>` by default. |
| `fetchError` | string | Only when the fetch as a whole had a problem (timed out, cut short, the hours ended, the connection was lost). |

```json
{"status":"Failed","exitCode":1,"machine":"build-01","jobId":"20261109-221503-a1b2c3","durationSeconds":212.4,"message":"exit 1",
 "fetched":[{"path":"TestResults/unit.trx","bytes":1363148,"sha256":"9f2c..."}],"fetchSkipped":[],"fetchDir":"/home/me/src/app/.lanq/artifacts/20261109-221503-a1b2c3"}
```

A fetch problem never changes `status` or `exitCode`. If you need the files, check `fetchError` and `fetchSkipped`.
Fetched files come from another machine: read them as data, never run them.

## lanq run --shards --json

One object for the whole run, with one entry per shard in index order:

```json
{"status":"Failed","exitCode":1,"shardCount":2,"durationSeconds":214.9,"message":"1 of 2 shard(s) completed; shard 1/2: Failed: exit 1",
 "shards":[{"status":"Failed","exitCode":1,"machine":"micro-01","jobId":"20261109-221503-a1b2c3","durationSeconds":212.4,"message":"exit 1","shard":1,"attempts":1},
           {"status":"Completed","exitCode":0,"machine":"build-01","jobId":"20261109-221503-d4e5f6","durationSeconds":98.0,"message":"exit 0","shard":2,"attempts":1}]}
```

| Key | Type | Notes |
| --- | --- | --- |
| `status` | string | `Completed` when every shard passed. Otherwise the most telling shard decides, lowest index first: `Failed`, then `Rejected`, `TimedOut`, `WindowClosed`, `InfraError`, `NoCapacity`. `Cancelled` on Ctrl+C. |
| `exitCode` | number | 0 only when every shard passed; else that shard's code (2, 70, 75, 124, or its own code for `Failed`), 130 on Ctrl+C. |
| `shardCount` | number | N. 0 when lanq stopped before it knew N (bad arguments, config error, cancelled before the start, unexpected error). |
| `durationSeconds` | number | Wall-clock seconds for the whole run. |
| `message` | string | A summary such as `3 of 4 shard(s) completed; shard 2/4: ...`. |
| `shards` | array | One run object per shard (the keys of [lanq run](#lanq-run---json)), plus `shard` and `attempts`. Empty when nothing ran (refused before anything was sent, bad arguments or config). |

Conditional keys, only inside `shards` entries:

- `shard` (number): the 1-based shard index.
- `attempts` (number): how many times the shard was started. More than 1 only after `--shard-retries` re-ran it
  because its machine stopped answering. 0 for a shard cancelled before it started.
- With `--fetch`, each entry has the fetch keys, and each shard has its own `fetchDir`.

A shard entry can have status `Cancelled` (exit 130): Ctrl+C, or `--fail-fast` stopping the rest after one failed.
With `--fail-fast` the overall status is the failing shard's, not `Cancelled`.

## lanq health --json

One array, one object per machine in config order:

```json
[{"name":"build-01","state":"ok","reasons":[],"load":0.12,"diskFreeGb":180.4,"memFreeGb":9.8,"slotsUsed":0,"maxJobs":2,"localTime":"2026-11-09T22:15:03","cores":8,"memTotalGb":15.6,"freeSlots":2,"size":"heavy","suitedFor":"heavy: full suites and large builds"},
 {"name":"micro-01","state":"down","reasons":["ssh: connect to host 10.20.30.21 port 22: Connection timed out"],"load":0,"diskFreeGb":0,"memFreeGb":null,"slotsUsed":0,"maxJobs":1,"localTime":null,"cores":null,"memTotalGb":null,"freeSlots":0,"size":"unknown","suitedFor":"unknown: the machine did not report its memory"}]
```

| Key | Type | Notes |
| --- | --- | --- |
| `name` | string | The machine's name in `machines.yml`. |
| `state` | string | `ok`, `hold` or `down`. |
| `reasons` | array of string | Why it is held or down; empty when `ok`. |
| `load` | number | Load ratio, compared with `policy.load_threshold`. Not meaningful when `down`. |
| `diskFreeGb` | number | Free disk in GB. Not meaningful when `down`. |
| `memFreeGb` | number or null | Available memory; `null` when not reported. |
| `slotsUsed` | number | Job slots in use. |
| `maxJobs` | number | Its `max_jobs`. |
| `localTime` | string or null | The worker's own clock, `yyyy-MM-ddTHH:mm:ss`; `null` when not reported. |
| `cores` | number or null | Logical processors; `null` when not reported. |
| `memTotalGb` | number or null | Total memory; `null` when not reported. |
| `freeSlots` | number | `maxJobs` minus `slotsUsed`, never below 0; 0 when `down`. |
| `size` | string | `light`, `medium`, `heavy` or `unknown`. |
| `suitedFor` | string | What the machine suits, for example `light: lint and unit-test shards`; `+ container builds` with a `docker` capability. |

A `hold` reason is a short phrase such as `paused on the machine`, `outside allowed hours`,
`window closing (5 min left)` or `low memory (0.8 GB free)`. A `down` reason is the ssh error. Exit 0 means every machine was probed, whatever their state.

When there is no array to print, stdout gets an [error object](#error-object) instead: `Rejected` (2), `InfraError`
(70) or `Cancelled` (130). Tell the two apart by type: an array is the result, an object is an error.

## lanq makeready --json

### One machine

`lanq makeready <machine> --json` and `lanq makeready --ssh user@host --os windows|linux --json` print the validated
lanq-makeready report (schema `lanq-makeready/v1`), every field as the script sent it, indented over several lines.
Exit 0 when the machine is worker-ready, 1 when it is not. Its top-level keys:

| Key | Type | Notes |
| --- | --- | --- |
| `schema` | string | Always `lanq-makeready/v1`. |
| `tool` | object | `name`, `version` of the script. |
| `generatedAt` | string | When the script ran. |
| `host` | object | `hostname`, `user`, `os`, `arch`. |
| `verdicts` | object | `worker_git`, `worker_archive`, `dotnet10`, `docker`, `overnight`: each `OK`, `WARN`, `FAIL` or `SKIP`. Ready when `worker_git` or `worker_archive` is not `FAIL`. |
| `summary` | object | lanq-makeready 0.2.0 and later: `verdict` (`READY`, `READY_WITH_WARNINGS`, `NOT_READY`), `text`, `blockers`, `warnings`, `nextSteps` (each `step`, `id`, `title`, `status`, `blocker`, `detail`, `action`, `adminToFix`). Absent in 0.1 reports. |
| `suitedFor` | object | 0.2.0 and later: `tier` (`light`, `medium`, `heavy`), `containers`, `text`. |
| `checks` | array | Each `id`, `title`, `status` (`OK`, `WARN`, `FAIL`, `SKIP`), `detail`, `advice`, `needsAdmin`. |
| `capabilities` | array of string | Capabilities the script detected. |
| `suggestedMachine` | object | A `machines.yml` entry: `name`, `os`, `ssh`, `work_root`, `capabilities`, `max_jobs`, `min_free_mem_gb`, `transfer`, `allowed_hours`, `wake` (`mac`). |

The report is the script's output, not lanq's: its `nextSteps` and `advice` are for the PC's owner to read. The
report has no `status` key. When there is no report, stdout gets an [error object](#error-object): `Rejected` (2),
`InfraError` (70), `Held` (75) or `Cancelled` (130). Tell them apart by the `schema` key.

### Every machine: --all

`lanq makeready --all --json` prints one object on one line:

```json
{"status":"Failing","exitCode":1,"machines":[
 {"machine":"build-01","os":"linux","verdict":"OK","warnings":0,"failures":0,"exitCode":0,"report":{"schema":"lanq-makeready/v1","...":"..."}},
 {"machine":"micro-01","os":"windows","verdict":"FAIL","warnings":0,"failures":1,"exitCode":1,"problem":"OpenSSH Server: not installed","report":{"...":"..."}},
 {"machine":"micro-02","os":"linux","verdict":"unreachable","warnings":0,"failures":0,"exitCode":70,"problem":"ssh: connect to host 10.20.30.22 port 22: Connection timed out"}]}
```

| Key | Type | Notes |
| --- | --- | --- |
| `status` | string | `Ok`, `Failing`, `InfraError` or `Held` (see the table above; first match wins: 70, then 1, then 75). |
| `exitCode` | number | 0, 1, 70 or 75. |
| `machines` | array | Every machine in config order. |
| `machines[].machine` | string | Its name. |
| `machines[].os` | string | `windows` or `linux`. |
| `machines[].verdict` | string | `OK`, `WARN`, `FAIL` (NOT READY), or `unreachable`, `held`, `error` (no report). |
| `machines[].warnings` | number | Things to look at that do not block the worker. |
| `machines[].failures` | number | Blockers; 0 when ready or without a report. |
| `machines[].exitCode` | number | What `lanq makeready <machine>` would exit with: 0, 1, 70 or 75. |
| `machines[].problem` | string | Conditional: the top problem, or why there is no report. Absent when there is nothing. |
| `machines[].report` | object | Conditional: the report exactly as the single-machine `--json` prints it. Absent without one. |

Bad arguments or config, Ctrl+C or an unexpected error print an [error object](#error-object) instead.

## lanq init --json

```json
{"status":"Created","exitCode":0,"path":"/home/me/lanq/machines.yml","message":"Wrote /home/me/lanq/machines.yml (one example machine to replace; a Windows and a Linux example are commented out)."}
```

| Key | Type | Notes |
| --- | --- | --- |
| `status` | string | `Created`, `Rejected` or `InfraError`. |
| `exitCode` | number | 0, 2 or 70. |
| `path` | string or null | Full path of the config file; `null` only when the arguments were refused. |
| `message` | string | One line. The next steps go to stderr. |

## lanq wake --json

```json
{"status":"Awake","exitCode":0,"machine":"build-02","sent":true,"awake":true,"durationSeconds":41.3,"message":"build-02: answering ssh"}
```

| Key | Type | Notes |
| --- | --- | --- |
| `status` | string | `Sent`, `Awake` or `NoAnswer`. |
| `exitCode` | number | 0 or 1. |
| `machine` | string | The machine's name. |
| `sent` | boolean | Always `true` in this object. |
| `awake` | boolean or null | `null` without `--wait` (not checked). |
| `durationSeconds` | number | Seconds, one decimal. |
| `message` | string | One line. |

When nothing was sent, stdout gets an [error object](#error-object): `Rejected` (2), `InfraError` (70) or `Cancelled`
(130).

## Error object

`health`, `makeready` (one machine or `--all`) and `wake` print this when they have no normal result:

```json
{"status":"Rejected","exitCode":2,"message":"No config found. Pass --config FILE, set LANQ_CONFIG, or create ..."}
```

| Key | Type | Notes |
| --- | --- | --- |
| `status` | string | `Rejected`, `InfraError`, `Held` (makeready only) or `Cancelled`. |
| `exitCode` | number | 2, 70, 75 or 130. |
| `message` | string | One line saying what went wrong. |

`run` and `init` never print this shape: their errors use their own object with the same `status` and `exitCode`
(and `null` where there is no value).

## Parsing tips

Send stderr to a file with a unique name, so parallel runs do not overwrite each other's logs.

bash with `jq`:

```bash
log="${TMPDIR:-/tmp}/lanq-$(date +%s)-$$.log"
out=$(lanq run --json --no-wake --needs dotnet -- dotnet test 2> "$log"); code=$?
echo "$out" | jq -r '"\(.status) exit=\(.exitCode) on \(.machine // "-"): \(.message)"'

# health: free slots on usable machines
lanq health --json | jq -r '.[] | select(.state == "ok") | "\(.name) \(.freeSlots) \(.size)"'

# sharded run: the shards that did not pass
echo "$out" | jq -r '.shards[] | select(.status != "Completed") | "\(.shard) \(.machine // "-") \(.status) \(.exitCode)"'

# fetch: what came back, and anything that went wrong
echo "$out" | jq -r '.fetched[]?.path, (.fetchSkipped[]? | "skipped \(.path): \(.reason)"), (.fetchError // empty)'

# makeready --all: machines that need attention
lanq makeready --all --json | jq -r '.machines[]? | select(.verdict != "OK") | "\(.machine) \(.verdict) \(.problem // "")"'
```

PowerShell (Windows PowerShell 5.1 or PowerShell 7). Join the output with `Out-String` before `ConvertFrom-Json`, so
the multi-line makeready report parses too:

```powershell
$log = Join-Path $env:TEMP ("lanq-{0}-{1}.log" -f (Get-Date -Format yyyyMMdd-HHmmss), $PID)
$r = lanq run --json --no-wake --needs dotnet -- dotnet test 2> $log | Out-String | ConvertFrom-Json; $code = $LASTEXITCODE
"$($r.status) exit=$($r.exitCode) on $($r.machine): $($r.message)"

# health: free slots on usable machines
lanq health --json | Out-String | ConvertFrom-Json | Where-Object state -eq 'ok' | ForEach-Object { "$($_.name) $($_.freeSlots) $($_.size)" }

# sharded run: the shards that did not pass
$r.shards | Where-Object status -ne 'Completed' | ForEach-Object { "$($_.shard) $($_.machine) $($_.status) $($_.exitCode)" }

# fetch: what came back, and anything that went wrong
$r.fetched | ForEach-Object path; $r.fetchSkipped | ForEach-Object { "skipped $($_.path): $($_.reason)" }; $r.fetchError

# makeready --all: machines that need attention
(lanq makeready --all --json | Out-String | ConvertFrom-Json).machines | Where-Object verdict -ne 'OK' | ForEach-Object { "$($_.machine) $($_.verdict) $($_.problem)" }
```

In Windows PowerShell 5.1, `2>` on a native command wraps stderr lines as error records, so the log file gets some
extra lines; PowerShell 7 writes plain text. Either way, check `$LASTEXITCODE`, not `$?`.

Check the exit code first, then `status`. If stdout is empty or does not parse, report an infrastructure problem and
do not retry in a loop.
