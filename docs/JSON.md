# JSON output

`--json` is how scripts and coding agents should read `lanq`. This page is the contract: what each command prints,
which keys it has, and what each status and exit code means.

These commands take `--json`: `lanq run` (also with `--shards` and `--dry-run`), `lanq health` (also with `--needs`),
`lanq makeready` (one machine, `--ssh`, `--all` or `--add`), `lanq init`, `lanq wake`, `lanq doctor` (also with
`--remote`) and `lanq stats` (the last two since 0.7.0). `lanq unlock` and `lanq makeready --setup` have no JSON output.

Every object lanq prints carries [`nextSteps`](#nextsteps): what to do next, as data. The one exception is the
single-machine `lanq makeready --json` report, which is the lanq-makeready script's own report.

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
  out entirely when they do not apply (`shard`, `attempts`, `retryable`, `excluded`, `logTail` and the `--fetch` keys
  on a run, `shardTiming` on a `--shards` run, `command` and `argv` in a next step, `problem` and `report` in
  `makeready --all`, `commandsMore` in `lanq stats`). In a dry run, `shardCountWhy` and `shardTiming` are always
  there and `null` when they do not apply. Inside a makeready report, keys without a value are left out, never `null`.
- **Key order is not part of the contract.** Look keys up by name.
- **Strings are sanitised.** Messages and anything that came from a worker have control, bidi and zero-width
  characters replaced by `?`. The serializer may also escape characters such as `<`, `>`, `&` and `'` as `<` and
  so on, so read strings through a JSON parser, not with grep.
- **Fields are only ever added.** A release may add keys, statuses, reason codes and `action` values. It does not
  remove, rename or retype a key; if that ever had to happen, [CHANGELOG.md](../CHANGELOG.md) would say so first.
  Ignore keys you do not know, treat an unknown `status` as an error, judge an unknown reason code by its `permanent`
  field, and treat an unknown `action` as `ask_developer`.

If lanq is killed outright (not Ctrl+C), stdout may be empty. Treat empty or unparsable stdout as an infrastructure
error and do not guess the result.

## Status and exit codes

| Command | `status` | Exit | Meaning |
| --- | --- | --- | --- |
| `run` | `Completed` | 0 | The job passed. |
| `run` | `Failed` | the job's own code | The job ran and exited non-zero. |
| `run` | `Rejected` | 2 | Bad arguments, a config error, or refused: not on the allowlist, a permission-skipping flag, a secret in the payload or command line, or a command the machine cannot run. Nothing ran. |
| `run` | `NoCapacity` | 75 | No eligible machine within `--wait`, or the machine's allowed hours were about to end after upload. `retryable` says whether waiting can help; when it is `false` lanq ends at once. |
| `run` | `InfraError` | 70 | ssh, git or snapshot trouble, a lost connection, or an unexpected error. |
| `run` | `TimedOut` | 124 | The job ran past `--timeout`. |
| `run` | `WindowClosed` | 124 | The machine's allowed hours (plus grace) ended while the job ran. |
| `run` | `Cancelled` | 130 | Ctrl+C. The job was cleaned up where it had started. |
| `run --shards` | any of the above | as above | Overall result; see [lanq run --shards](#lanq-run---shards---json). |
| `run --dry-run` | `WouldRun` | 0 | `lanq run` would start now. Nothing was sent. |
| `run --dry-run` | `NoCapacity` | 75 | No machine could take the job now (`retryable` as for a run). |
| `run --dry-run` | `Rejected`, `InfraError`, `Cancelled` | 2, 70, 130 | As for a run; `InfraError` here is a failed snapshot or an unexpected error. |
| `health` | (an array) | 0 | Every machine was probed. Machines that are down or held are in the array; that is not an error. |
| `health --needs`/`--mem` | (an array) | 0 or 75 | 0 when at least one machine could take such a job now, 75 when none could. |
| `makeready <machine>` | (the report) | 0 or 1 | 0 worker-ready, 1 not ready. The report has no `status` key. |
| `makeready --all` | `Ok` | 0 | Every machine is ready (warnings alone still exit 0). |
| `makeready --all` | `Failing` | 1 | At least one machine is NOT READY. |
| `makeready --all` | `InfraError` | 70 | A machine was unreachable or sent an unusable report. |
| `makeready --all` | `Held` | 75 | A machine is paused or outside its allowed hours (and none of the above). |
| `makeready --add` | `Added` | 0 | The READY machine was added to `machines.yml`. |
| `makeready --add` | `NotAdded` | 1 | NOT READY, or the prompt was declined. Nothing was written. |
| `makeready --add` | `Rejected` | 2 | Bad arguments, config, a duplicate name or host, or a file lanq does not edit by itself. Nothing was written. |
| `makeready --add` | `InfraError` | 70 | ssh failed, the report was unusable, or the file could not be written. |
| `makeready --add` | `Cancelled` | 130 | Ctrl+C. |
| `init` | `Created` | 0 | The starter `machines.yml` was written. |
| `init` | `Rejected` | 2 | The path exists (file, folder or link) or bad arguments. |
| `init` | `InfraError` | 70 | The file could not be written. |
| `wake` | `Sent` | 0 | The magic packet was sent (no `--wait`). |
| `wake` | `Awake` | 0 | Sent, and the machine answered ssh (`--wait`). |
| `wake` | `NoAnswer` | 1 | Sent, but no ssh answer within `wake.wait_seconds` (`--wait`). |
| `doctor` | `Ok` | 0 | No check is `warn` or `fail` (`hold` and `skip` are fine). |
| `doctor` | `Warnings` | 1 | At least one `warn`, no `fail`. |
| `doctor` | `Failing` | 1 | At least one `fail`, a config that does not load included. |
| `doctor` | `Rejected` | 2 | Bad arguments. `checks` is empty. |
| `doctor` | `InfraError` | 70 | An unexpected error. `checks` has the checks that had finished. |
| `doctor` | `Cancelled` | 130 | Ctrl+C. `checks` has the checks that had finished. |
| `stats` | `Ok` | 0 | The job log was read. No job log at all is `Ok` with 0 jobs. |
| `stats` | `Rejected` | 2 | Bad arguments. |
| `stats` | `InfraError` | 70 | The job log exists but could not be read, or an unexpected error. |
| `stats` | `Cancelled` | 130 | Ctrl+C. |
| error object | `Rejected` | 2 | `health`, `makeready`, `wake`: bad arguments or config; `wake`: unknown machine or no `wake:` setting. |
| error object | `InfraError` | 70 | `health`, `makeready`, `wake`: an unexpected error; `wake`: the packet could not be sent; `makeready <machine>`: ssh failed or the report was unusable. |
| error object | `Held` | 75 | `makeready <machine>` only: the machine is paused or outside its allowed hours (`--force` scans anyway). |
| error object | `Cancelled` | 130 | Ctrl+C. |

Several exit codes cover more than one case, so read `status`, not only the exit code:

- **124** is `TimedOut` or `WindowClosed`.
- **75** is `NoCapacity` (run and dry run), `Held` (makeready) or `Held` (makeready --all), or no eligible machine
  (an array) from `health --needs`.
- **70** is `InfraError` everywhere; for `makeready --all` the full object has `machines` and the error object does not.
- **1** is a failed job, a NOT READY machine, `NotAdded` from `makeready --add`, `NoAnswer` from `wake`, or
  `Warnings` or `Failing` from `doctor`.
- A job's own exit code can be any number, including 2, 70, 75, 124 or 130. `Failed` with exit 2 is the job's own
  failure, not a refusal.

## lanq run --json

The job's output streams to stderr. When the run is over, stdout gets one object:

```json
{"status":"Completed","exitCode":0,"machine":"build-01","jobId":"20261109-221503-a1b2c3","durationSeconds":84.2,"message":"exit 0",
 "logPath":"/home/me/.local/state/lanq/logs/20261109-221503-a1b2c3.log","nextSteps":[{"action":"none","reason":"the job completed","safeToRunUnattended":false}]}
```

| Key | Type | Notes |
| --- | --- | --- |
| `status` | string | `Completed`, `Failed`, `Rejected`, `NoCapacity`, `InfraError`, `TimedOut`, `WindowClosed` or `Cancelled`. |
| `exitCode` | number | lanq's own exit code, the same as the process exit code. |
| `machine` | string or null | The machine that took the job; `null` when none did. |
| `jobId` | string or null | `null` when lanq stopped before it made one (bad arguments, config error, Ctrl+C, unexpected error). A refusal by policy or `NoCapacity` still has one. |
| `durationSeconds` | number | Wall-clock seconds for the whole run, one decimal. |
| `message` | string | One line: `exit 1`, `timed out after 30 min`, the refusal or the error. |
| `logPath` | string or null | Always there. The full path of the job's local log, `<state>/logs/<jobId>.log`: every line lanq printed for the job (prepare, the job's output, the fetch summary), not the final status line. `null` when no job started (refused, `NoCapacity`, bad arguments). After Ctrl+C it names the started job's log. |
| `logTail` | array of string | Conditional: only with `--log-tail N` (1 to 200, needs `--json`). The log's last N lines, sanitised, each at most 4000 characters, read from at most the last 256 KB of the file. Empty when no job started. |
| `retryable` | boolean | Conditional: only when `status` is `NoCapacity`. See [NoCapacity](#nocapacity-retryable-and-excluded). |
| `excluded` | array | Conditional: only when `status` is `NoCapacity`. See [NoCapacity](#nocapacity-retryable-and-excluded). |
| `nextSteps` | array | Always there (at least one entry). See [nextSteps](#nextsteps). |

The state folder is `$LANQ_HOME`, else `%LOCALAPPDATA%\lanq` on Windows, else `$XDG_STATE_HOME/lanq`, else
`~/.local/state/lanq`. Since 0.7.0 lanq deletes its own job logs there after `policy.keep_logs_days` (default 30; 0
keeps them for ever), so a `logPath` from an old run may no longer exist. `logTail` puts the job's own output on stdout
inside the JSON, so ask for it only when you want to read it; it is sanitised like every other string.

A refused command line:

```json
{"status":"Rejected","exitCode":2,"machine":null,"jobId":null,"durationSeconds":0,"message":"lanq run does not take --force.","logPath":null,
 "nextSteps":[{"action":"fix_command","reason":"fix the command line: lanq run does not take --force.","safeToRunUnattended":false}]}
```

### NoCapacity: retryable and excluded

When `status` is `NoCapacity` (exit 75) the object adds two keys:

- `retryable` (boolean): `false` only when **every** machine has a permanent reason (see the table below), so no
  machine can ever take the job as configured and waiting will not help. A machine that is merely busy, held, asleep or
  unreachable keeps it `true`.
- `excluded` (array): every machine that did not take the job, in config order, each
  `{"machine", "reason", "why", "permanent"}`. `reason` is a code from the table below, `why` the same for people
  (for example `lacks gpu (capabilities: dotnet, linux)` or the ssh error), and `permanent` is `true` when waiting can
  never change it.

When `retryable` is `false`, lanq ends at once: it does not wait out `--wait` and never wakes a machine. When no machine
in `machines.yml` has every `--needs` capability it ends before the snapshot or any ssh call (a command line with
arguments Windows cannot take safely, on a pool of only Windows machines, is still `Rejected`, exit 2, first). The
message starts `No eligible machine, and no machine can ever take this job as configured (waiting will not help).`;
otherwise it starts `No eligible machine.` Each machine's `why` gives the reason waiting cannot cure first, so a machine
that is down and lacks `gpu` says `lacks gpu ...`, not its ssh error.

```json
{"status":"NoCapacity","exitCode":75,"machine":null,"jobId":"20261109-221503-a1b2c3","durationSeconds":0.1,
 "message":"No eligible machine, and no machine can ever take this job as configured (waiting will not help). micro-01: lacks gpu (capabilities: dotnet, linux)",
 "retryable":false,"excluded":[{"machine":"micro-01","reason":"missing_capability","why":"lacks gpu (capabilities: dotnet, linux)","permanent":true}],
 "logPath":null,"nextSteps":[
  {"action":"ask_developer","reason":"no machine can ever take this job as configured, so waiting will not help (micro-01: lacks gpu (capabilities: dotnet, linux)); a machine needs the capability or the memory, or the job needs other --needs or --mem","safeToRunUnattended":false},
  {"action":"run_health","reason":"see how each machine is judged for this job","command":"lanq health --needs gpu --json","argv":["lanq","health","--needs","gpu","--json"],"safeToRunUnattended":true}]}
```

### Reason codes

The same codes appear in `excluded`, in `lanq health --needs` rows and in the `machines` of a dry run. Codes may be
added; judge an unknown one by `permanent`.

| `reason` | Permanent | Meaning |
| --- | --- | --- |
| `missing_capability` | yes | The machine lacks a `--needs` capability (its config). |
| `windows_unsafe_args` | yes | A Windows machine, and the job's arguments hold characters cmd.exe would re-parse. |
| `mem_exceeds_total` | yes | `--mem` is above the machine's total memory. |
| `mem_unknown` | yes | With `--mem`: the machine answers but does not report its memory. |
| `tool_missing` | yes | git (or tar with `transfer: archive`) is missing on the machine. |
| `unreachable` | no | ssh failed (refused, timed out, key refused, unknown host key, name not resolved). |
| `asleep` | no | The machine looks asleep or switched off. |
| `unreadable` | no | The probe's answer could not be read. |
| `paused` | no | A `PAUSE` file is in its `work_root`. |
| `outside_hours` | no | Outside its `allowed_hours`. |
| `window_closing` | no | Fewer than `policy.min_window_minutes` of its allowed hours are left. |
| `low_disk` | no | Free disk below `policy.min_free_disk_gb`. |
| `low_memory` | no | Available memory below `min_free_mem_gb`. |
| `memory_pressure` | no | Linux memory pressure above `policy.max_mem_pressure`. |
| `load` | no | Load above `policy.load_threshold`. |
| `mem_below_request` | no | Available memory now is below `--mem`. |
| `busy` | no | Every slot is taken. |
| `avoided` | no | `--shards` only: an earlier attempt of the shard lost the connection there, and it may still be running. |
| `held` | no | Any other hold. |

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
| `retryable`, `excluded` | | Conditional: only when `status` is `NoCapacity`, at the top level, as for [one run](#nocapacity-retryable-and-excluded). |
| `shardTiming` | object | Conditional: only with `--shards auto --target-minutes M`, and only once N was picked: how N came from earlier runs ([shardTiming](#shardtiming)). Left out otherwise, also on a refused command line. |
| `nextSteps` | array | Always there, for the whole run. See [nextSteps](#nextsteps). |

Conditional keys, only inside `shards` entries:

- `shard` (number): the 1-based shard index.
- `attempts` (number): how many times the shard was started. More than 1 only after `--shard-retries` re-ran it
  because its machine stopped answering. 0 for a shard cancelled before it started.
- With `--fetch`, each entry has the fetch keys, and each shard has its own `fetchDir`.

Each entry in `shards` has its own `logPath` (the log of its last attempt, `null` when it never started) and, with
`--log-tail N`, its own `logTail`. The entries carry no `nextSteps`, `retryable` or `excluded`; the top level does. In
text mode lanq prints `[lanq] log: <path> (shard i/N)` for each shard that did not complete.

A shard entry can have status `Cancelled` (exit 130): Ctrl+C, or `--fail-fast` stopping the rest after one failed.
With `--fail-fast` the overall status is the failing shard's, not `Cancelled`.

## lanq run --dry-run --json

`lanq run --dry-run` says what `lanq run` would do now, by the same code, and does none of it: the same command-line
checks, the same snapshot and secret scan of the uncommitted changes (no bundle or archive is built; git still writes
the snapshot's objects to the repository's object store, as a run does), one probe of every machine and the same
choice. It claims no slot, sends nothing, writes no log and no `jobs.jsonl` line, fetches nothing and never sends a
Wake-on-LAN packet. Every `lanq run` option is accepted and checked. With `--json` the plan text goes to stderr and
stdout gets one object, also for bad arguments or config:

```json
{"status":"WouldRun","exitCode":0,"dryRun":true,"message":"would run on build-01","machine":"build-01","ranked":["build-01","micro-02"],
 "shardCount":null,"shardCountAuto":null,"shardCountWhy":null,"shardTiming":null,"placement":null,
 "machines":[{"machine":"build-01","eligible":true,"reason":null,"why":null,"permanent":false,"freeSlots":2,"jobSlots":2},
  {"machine":"micro-02","eligible":true,"reason":null,"why":null,"permanent":false,"freeSlots":1,"jobSlots":1},
  {"machine":"win-01","eligible":false,"reason":"missing_capability","why":"lacks linux (capabilities: dotnet, windows)","permanent":true,"freeSlots":1,"jobSlots":0}],
 "wouldWake":null,"retryable":null,
 "nextSteps":[{"action":"proceed","reason":"lanq run would start now as shown; run it","command":"lanq run --json --no-wake --needs dotnet,linux -- dotnet test -m:1","argv":["lanq","run","--json","--no-wake","--needs","dotnet,linux","--","dotnet","test","-m:1"],"safeToRunUnattended":true}]}
```

Every key is always there (`null` where it does not apply), except `logTail`.

| Key | Type | Notes |
| --- | --- | --- |
| `status` | string | `WouldRun` (exit 0), or the status the run would end with: `NoCapacity` (75), `Rejected` (2), `InfraError` (70: the snapshot failed, or an unexpected error), `Cancelled` (130). |
| `exitCode` | number | The dry run's own exit code. |
| `dryRun` | boolean | Always `true`. |
| `message` | string | One line. |
| `machine` | string or null | The machine the job (or shard 1) would go to. |
| `ranked` | array of string | The machines that could take the job now, in the order `lanq run` would try them. Empty when none could. |
| `shardCount` | number or null | `--shards` only: N (the choice for `auto`). |
| `shardCountAuto` | boolean or null | `--shards` only: `true` for `--shards auto`. |
| `shardCountWhy` | string or null | `--shards auto` only: why that N, as the `[lanq] --shards auto:` line of `lanq run` says it (with `--target-minutes`, the timing reasoning first, then the free slots). `null` for `--shards N` and without `--shards`. |
| `shardTiming` | object or null | `--shards auto --target-minutes M` only: how N was picked from earlier runs ([shardTiming](#shardtiming)). `null` otherwise. |
| `placement` | array or null | `--shards` only: where the shards would start now, each `{"machine", "shards": [numbers]}`; empty when none could start. |
| `machines` | array | Every machine as judged for the job, in config order: `{machine, eligible, reason, why, permanent, freeSlots, jobSlots}` ([reason codes](#reason-codes)). Empty when the run was refused before any machine was looked at. |
| `wouldWake` | string or null | The sleeping machine `lanq run` would wake (with `--wait` above 0, no `--no-wake`, and no awake machine able to take the job). |
| `retryable` | boolean or null | `NoCapacity` only, as for [a run](#nocapacity-retryable-and-excluded). |
| `logTail` | array | Conditional: only with `--log-tail`, and then always empty (a dry run starts no job). |
| `nextSteps` | array | `WouldRun` proceeds with the same command without `--dry-run`. See [nextSteps](#nextsteps). |

A dry run cannot foresee an archive that fails to build or a slot another run takes between the probe and a real
start. In text mode the plan goes to stdout, and the last stderr line is `[lanq] WouldRun: ...` (or the other status).
A dry run reads `jobs.jsonl` for `--target-minutes` and never writes it.

### shardTiming

With `--shards auto --target-minutes M`, the dry run (always) and the `--shards` run (conditional) carry how N was
picked. Every key is always there, `null` when it does not apply. [SHARDING.md](SHARDING.md#choosing-n-from-history---target-minutes)
explains the rule.

```json
{"targetMinutes":5,"command":"npx jest --shard={shard}/{shards}","dir":".","basis":"history","runsUsed":3,"lastRunUtc":"2026-11-08T10:04:00Z",
 "workMinutes":10.7,"overheadSeconds":30,"slowestMachine":"tiny-01","speedFactor":1.62,"wanted":4,"freeSlots":5,"capped":null,"picked":4,
 "expectedShardMinutes":4.8,"why":"--target-minutes 5: 3 earlier runs, about 10.7 min of work + 30 s per shard, slowest machine tiny-01 x1.62: about 4.8 min each"}
```

| Key | Type | Notes |
| --- | --- | --- |
| `targetMinutes` | number | M (1 to 1440). |
| `command` | string | The command's key in `jobs.jsonl`: its arguments with `{shard}` and `{shards}` kept as they are. |
| `dir` | string | The `--dir`, normalised (`/` separators, `.` for the repository root). |
| `basis` | string | `history` (N from earlier runs), `no_history` (no usable run: N is the free slots, as plain `--shards auto`) or `overhead` (the per-shard overhead leaves less than a tenth of M for the work: every free slot). Values may be added. |
| `runsUsed` | number | How many earlier runs the estimate comes from: at most the newest 10 of the last 90 days. 0 with `no_history`. |
| `lastRunUtc` | string or null | The start of the newest job of those runs, UTC. |
| `workMinutes` | number or null | The whole command's own run time on a machine of speed factor 1, one decimal. |
| `overheadSeconds` | number or null | Per shard: wall-clock minus the command's own run time (slot, upload, prepare, fetch, cleanup), one decimal. |
| `slowestMachine` | string or null | The slowest machine the N shards would be dealt to now; it decides N. |
| `speedFactor` | number or null | Its speed factor (1.62 means 62% slower than typical). |
| `wanted` | number or null | The N that M needs before any cap. `null` with `no_history` and `overhead`. |
| `freeSlots` | number | The free slots that can take the job now. |
| `capped` | string or null | `free_slots` or `max_shards` (64) when that cap made N lower than `wanted` (with `no_history` or `overhead`: `max_shards` when over 64 slots were free); else `null`. |
| `picked` | number | N, the same as `shardCount`. |
| `expectedShardMinutes` | number or null | About how long each shard takes on the slowest machine: (overhead + factor x work / N) / 60, one decimal. |
| `why` | string | The reasoning for people, starting with `--target-minutes M:`. |

## lanq health --json

One array, one object per machine in config order:

```json
[{"name":"build-01","state":"ok","reasons":[],"load":0.12,"diskFreeGb":180.4,"memFreeGb":9.8,"slotsUsed":0,"maxJobs":2,"localTime":"2026-11-09T22:15:03","cores":8,"memTotalGb":15.6,"loadKnown":true,"freeSlots":2,"size":"heavy","suitedFor":"heavy: full suites and large builds"},
 {"name":"micro-01","state":"down","reasons":["ssh: connect to host 10.20.30.21 port 22: Connection timed out"],"load":0,"diskFreeGb":0,"memFreeGb":null,"slotsUsed":0,"maxJobs":1,"localTime":null,"cores":null,"memTotalGb":null,"loadKnown":true,"freeSlots":0,"size":"unknown","suitedFor":"unknown: the machine did not report its memory"}]
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
| `loadKnown` | boolean | `false` when the worker could not measure its load (a Windows account denied WMI where the fallback is refused too): `load` is then 0 and means nothing, and load alone never holds the machine. `true` otherwise, also when `down` (0.8.0). |
| `freeSlots` | number | `maxJobs` minus `slotsUsed`, never below 0; 0 when `down`. |
| `size` | string | `light`, `medium`, `heavy` or `unknown`. |
| `suitedFor` | string | What the machine suits, for example `light: lint and unit-test shards`; `+ container builds` with a `docker` capability. |
| `nextSteps` | array | Always there: what to do about this machine (`none` when it can take work). See [nextSteps](#nextsteps). |

A `hold` reason is a short phrase such as `paused on the machine`, `outside allowed hours`,
`window closing (5 min left)` or `low memory (0.8 GB free)`. A `down` reason is the ssh error. Exit 0 means every machine was probed, whatever their state.

### With --needs and --mem

`lanq health --needs a,b [--mem GB] --json` judges every machine for such a job by the rules `lanq run` uses, from
the same probe. Each row has the keys above plus these five, always present:

| Key | Type | Notes |
| --- | --- | --- |
| `eligible` | boolean | The machine could take the job now. |
| `why` | string or null | Why not, for people (`lacks gpu (capabilities: dotnet, linux)`, `busy (slots 1/1)`, the ssh error); `null` when eligible. |
| `reason` | string or null | The [reason code](#reason-codes); `null` when eligible. |
| `permanent` | boolean | `true` when waiting can never change it. `false` when eligible. |
| `jobSlots` | number | How many copies (or shards) of the job it could take now, `--mem` counted per copy; 0 when not eligible. |

```json
[{"name":"build-01","state":"ok","reasons":[],"load":0.12,"diskFreeGb":180.4,"memFreeGb":9.8,"slotsUsed":0,"maxJobs":2,"localTime":"2026-11-09T22:15:03","cores":8,"memTotalGb":15.6,"loadKnown":true,"freeSlots":2,"size":"heavy","suitedFor":"heavy: full suites and large builds",
  "nextSteps":[{"action":"none","reason":"build-01 can take such a job now","safeToRunUnattended":false}],"eligible":true,"why":null,"reason":null,"permanent":false,"jobSlots":2}]
```

Exit `0` when at least one machine is eligible, `75` when none is. Without `--needs` and `--mem` the rows are exactly
the plain ones and the exit code is always `0`. `--wake` works here too, as without `--needs`.

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

The report is the script's output, not lanq's: its `summary.nextSteps` and `advice` are for the PC's owner to read,
and it has no lanq `nextSteps` (this is the one `--json` object without them). The report has no `status` key. When
there is no report, stdout gets an [error object](#error-object) with lanq's `nextSteps`: `Rejected` (2),
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
| `machines[].nextSteps` | array | What to do about this machine: `none` when OK or WARN. |
| `nextSteps` | array | For the whole check. See [nextSteps](#nextsteps). |

Bad arguments or config, Ctrl+C or an unexpected error print an [error object](#error-object) instead.

## lanq makeready --add --json

`lanq makeready --ssh user@host --os windows|linux --add --name NAME --yes --json` (`--json` always needs `--yes`)
prints one object on every path. Every key is always there, `null` when it has no value.

```json
{"status":"Added","exitCode":0,"path":"/home/me/.config/lanq/machines.yml","machine":"micro-01",
 "entry":["  - name: micro-01","    os: windows","    ssh: build@10.20.30.21","    work_root: C:/lq","..."],"afterLine":24,
 "health":{"name":"micro-01","state":"ok","...":"..."},"report":{"schema":"lanq-makeready/v1","...":"..."},
 "message":"Added micro-01 to /home/me/.config/lanq/machines.yml.",
 "nextSteps":[{"action":"run_doctor","reason":"check the new machine and this computer's setup","command":"lanq doctor --remote --json","argv":["lanq","doctor","--remote","--json"],"safeToRunUnattended":true}]}
```

| Key | Type | Notes |
| --- | --- | --- |
| `status` | string | `Added`, `NotAdded`, `Rejected`, `InfraError` or `Cancelled` (see the [table](#status-and-exit-codes)). |
| `exitCode` | number | 0, 1, 2, 70 or 130. |
| `path` | string or null | The full path of `machines.yml`; `null` before it was found. |
| `machine` | string or null | The new machine's name (`--name`); `null` when the arguments were refused. |
| `entry` | array of string or null | The lines written (`Added`) or that would have been written, exactly; `null` before there was an entry. |
| `afterLine` | number or null | The 1-based line of `machines.yml` the entry follows; `null` without an entry. |
| `health` | object or null | One read-only probe of the added machine, as a [`lanq health --json`](#lanq-health---json) row; `null` unless `Added`. |
| `report` | object or null | The validated `lanq-makeready/v1` report, as `lanq makeready --json` prints it; `null` when there was none. |
| `message` | string | One line. |
| `nextSteps` | array | `Added` suggests `lanq doctor --remote --json` (and asks the developer to delete `example-01` when `lanq init`'s placeholder is still there). A NOT READY PC asks the developer, with `lanq makeready --ssh ...` for the details. Never `--add` or `--yes`. |

## lanq init --json

```json
{"status":"Created","exitCode":0,"path":"/home/me/lanq/machines.yml","message":"Wrote /home/me/lanq/machines.yml (one example machine to replace; a Windows and a Linux example are commented out)."}
```

| Key | Type | Notes |
| --- | --- | --- |
| `status` | string | `Created`, `Rejected` or `InfraError`. |
| `exitCode` | number | 0, 2 or 70. |
| `path` | string or null | Full path of the config file (the per-user config file unless `--config` or `LANQ_CONFIG` named another); `null` only when the arguments were refused. |
| `message` | string | One line. The next steps for people go to stderr. |
| `nextSteps` | array | `Created`: replace `example-01` (for the developer) and `lanq doctor --json --config <path>`. |

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
| `nextSteps` | array | `Sent` suggests `lanq health --json`, never another wake. |

When nothing was sent, stdout gets an [error object](#error-object): `Rejected` (2), `InfraError` (70) or `Cancelled`
(130).

## lanq doctor --json

`lanq doctor --json` and `lanq doctor --remote [--force] --json` run the same checks as the text command. The text
lines go to stderr as they are found, then the `Next:` lines; stdout gets one object on every path. Without `--json`,
doctor prints exactly what it printed before 0.7.0 (one more line, `logs`, after `config`), on stdout, with no
`Next:` lines.

```json
{"status":"Failing","exitCode":1,"message":"13 checks: 11 ok, 1 warn, 1 fail, 0 skip, 0 hold","remote":true,
 "counts":{"ok":11,"warn":1,"fail":1,"skip":0,"hold":0},
 "checks":[{"scope":"local","machine":null,"name":"ssh","level":"ok","detail":"ssh: OpenSSH_9.6p1, OpenSSL 3.0.13","fix":null,"capability":null},
  {"...":"..."},
  {"scope":"remote","machine":"build-01","name":"git_version","level":"warn","detail":"git older than 2.32: global config not isolated (...)","fix":"update git on that machine to 2.32 or later","capability":null},
  {"scope":"remote","machine":"build-01","name":"capability","level":"fail","detail":"npm: 'npm --version' failed in the job environment (sh: 1: npm: not found). ...","fix":"add the tool's folder to path_prepend for this machine","capability":"npm"}],
 "nextSteps":[{"action":"ask_developer","reason":"lanq doctor found 1 fail and 1 warn check(s): build-01 git_version, build-01 capability npm; each check's fix (checks[].fix) says what to do, and the developer or the machine's owner does it","safeToRunUnattended":false}]}
```

Every key is always there.

| Key | Type | Notes |
| --- | --- | --- |
| `status` | string | `Ok`, `Warnings`, `Failing`, `Rejected`, `InfraError` or `Cancelled` (see the [table](#status-and-exit-codes)). |
| `exitCode` | number | 0, 1, 2, 70 or 130. |
| `message` | string | The counts (`13 checks: 11 ok, 1 warn, 1 fail, 0 skip, 0 hold`); the config's error when it does not load; the argument or unexpected error otherwise. |
| `remote` | boolean or null | Whether `--remote` was given; `null` when the command line did not parse. |
| `counts` | object | `{ok, warn, fail, skip, hold}`: how many checks have each level. |
| `checks` | array | One entry per text line, in the same order: local checks, then each machine's settings, then (with `--remote`) each machine's remote checks, in config order. Empty on `Rejected`. |
| `checks[].scope` | string | `local` (this computer), `machine` (a machine's settings, checked on this computer) or `remote` (checked on the machine over ssh). |
| `checks[].machine` | string or null | The machine's name; `null` for a `local` check. |
| `checks[].name` | string | What was checked (table below). Names may be added. |
| `checks[].level` | string | `ok`, `warn`, `fail`, `skip` or `hold`. |
| `checks[].detail` | string | The text line after its level and machine prefix, sanitised. |
| `checks[].fix` | string or null | A short remedy, for the developer or the machine's owner; `null` when there is none. |
| `checks[].capability` | string or null | The capability a `capability` check is about; `null` for every other check. |
| `nextSteps` | array | See below and [nextSteps](#nextsteps). |

The check names, with the levels each can have:

| `name` | `scope` | Levels | What it checks |
| --- | --- | --- | --- |
| `ssh`, `scp`, `git`, `ssh_keygen` | `local` | `ok`, `fail` | The program is on PATH (`ssh` and `git` with their version). |
| `state_folder` | `local` | `ok`, `fail` | The state folder is writable. |
| `config` | `local` | `ok`, `fail` | The config loads. A `fail` here is the last check: nothing after it runs. |
| `logs` | `local` | `ok`, `warn`, `skip` | lanq's job logs in `logs/`: count and size. `warn` over 2 GB with `policy.keep_logs_days` 0; `skip` when the folder could not be read. |
| `known_hosts` | `machine` | `ok`, `fail` | A `known_hosts` entry for the machine's host (and port). |
| `key_env` | `machine` | `ok`, `warn`, `fail` | Only with `key_env`: `fail` when the variable is not set or the key file is missing; `warn` when the key has no passphrase. |
| `work_root` | `machine` | `ok` | The `work_root`, OS and transfer mode, as configured. |
| `wake` | `machine` | `ok` | Only with `wake:`: the Wake-on-LAN setting. |
| `held` | `remote` | `hold` | Paused or outside its allowed hours, so not checked (without `--force`). |
| `script_size` | `remote` | `fail` | The doctor script is over the Windows command-line limit; nothing else is checked there. |
| `reachable` | `remote` | `fail` | ssh failed; nothing else is checked there. |
| `work_root_writable` | `remote` | `ok`, `warn`, `fail` | `warn` when `work_root` does not exist yet. |
| `transfer_tool` | `remote` | `ok`, `fail` | git (or tar with `transfer: archive`) in a non-login ssh session. |
| `git_version` | `remote` | `warn` | Only on a git machine with git older than 2.32. |
| `wmi` | `remote` | `warn` | Only on a Windows machine whose ssh user is denied WMI/CIM (0.8.0). lanq works without it; `fix` says what the owner could grant. |
| `capability` | `remote` | `ok`, `fail`, `skip` | One per capability: its tool answers in the job environment (`dotnet --version` needs an SDK); `skip` for a name lanq has no check for. When `dotnet` fails and the worker has an SDK in a folder the job's `PATH` does not reach, `detail` names it and `fix` is the `path_prepend:` (and `DOTNET_ROOT`) line to add (0.8.0). |

`nextSteps` for doctor:

- `Ok` with nothing held: `none`.
- Any `warn` or `fail`: one `ask_developer` step naming up to five `machine name` pairs (`local` for a local check)
  and pointing to `checks[].fix`. Never a command: only the developer or the machine's owner fixes keys,
  `known_hosts`, the config or a worker's tools.
- Machines held with `--remote`: also a `wait` step without a command. `--force` is never suggested.
- The config does not load: one `ask_developer` step.
- `Rejected`: `fix_command`. `InfraError`: `retry`, the same `lanq doctor --json` (with `--remote` if given), safe to
  run once. `Cancelled`: `none`.

## lanq stats --json

`lanq stats --json` reads the local job log (`jobs.1.jsonl`, then `jobs.jsonl`) and prints one object. It reads no
config except for `policy.keep_logs_days`, and never connects to anything. Times are UTC (ISO 8601); the text output
shows local time.

```json
{"status":"Ok","exitCode":0,"message":"412 jobs","files":["/home/me/.local/state/lanq/jobs.1.jsonl","/home/me/.local/state/lanq/jobs.jsonl"],
 "jobs":412,"wallMinutes":1830.4,"skippedLines":0,"oldestUtc":"2026-08-14T19:02:11Z","newestUtc":"2026-11-08T21:40:05Z",
 "logs":{"files":388,"megabytes":96.2,"keepDays":30},
 "machines":[{"machine":"big-01","jobs":290,"completed":271,"failed":15,"other":4,"wallMinutes":1214.7,"medianSeconds":182.4,"p90Seconds":410.2,
   "lastRunUtc":"2026-11-08T21:40:05Z","speedFactor":1,"speedSamples":244},
  {"machine":"tiny-01","jobs":122,"completed":117,"failed":5,"other":0,"wallMinutes":615.7,"medianSeconds":271.9,"p90Seconds":402.6,
   "lastRunUtc":"2026-11-08T21:39:58Z","speedFactor":1.62,"speedSamples":37}],
 "commands":[{"command":"npx jest --shard={shard}/{shards}","dir":".","runs":31,"jobs":124,"completed":118,"failed":5,"other":1,
   "medianSeconds":176.3,"p90Seconds":240.8,"lastRunUtc":"2026-11-08T21:40:05Z","lastShards":4,
   "timing":{"runsUsed":10,"workMinutes":10.7,"overheadSeconds":30,"suggestedShards":null}},
  {"command":"dotnet test","dir":null,"runs":40,"jobs":40,"completed":36,"failed":4,"other":0,"medianSeconds":95.2,"p90Seconds":130.5,
   "lastRunUtc":"2026-10-30T18:12:44Z","lastShards":null,"timing":null}],
 "nextSteps":[{"action":"none","reason":"lanq stats only reads the local job log; nothing to do","safeToRunUnattended":false}]}
```

Every key is always there except `commandsMore`.

| Key | Type | Notes |
| --- | --- | --- |
| `status` | string | `Ok`, `Rejected`, `InfraError` or `Cancelled` (see the [table](#status-and-exit-codes)). |
| `exitCode` | number | 0, 2, 70 or 130. |
| `message` | string | `412 jobs`, or what went wrong. |
| `files` | array of string | The job log files read, full paths, oldest first. Empty when there is no job log. |
| `jobs` | number | Every job in the log, old lines included. |
| `wallMinutes` | number | Their wall-clock time, one decimal. |
| `skippedLines` | number | Lines that were not a job record (a torn line, a hand edit): skipped and counted. stderr then has `[lanq] stats: N unreadable line(s) in jobs.jsonl skipped`. |
| `oldestUtc`, `newestUtc` | string or null | When the oldest and newest job started; `null` with no jobs. |
| `logs` | object | `{files, megabytes, keepDays}`: lanq's own job logs in `logs/` (count, size in MB with one decimal) and `policy.keep_logs_days` (0 = kept for ever; 30 when the config does not load; null in the `Rejected` object for bad arguments, where the config is never read). |
| `machines` | array | One entry per machine, by name (`-` for jobs that never got a machine). |
| `machines[].machine` | string | The name. |
| `machines[].jobs`, `.completed`, `.failed`, `.other` | number | Every job, and how many were `Completed`, `Failed` or anything else (`Rejected`, `TimedOut`, `InfraError`, ...). |
| `machines[].wallMinutes` | number | Wall-clock minutes, one decimal. |
| `machines[].medianSeconds`, `.p90Seconds` | number or null | Median and 90th percentile of the wall-clock seconds of its `Completed` jobs, by nearest rank (the median of an even count is the lower middle value), one decimal; `null` when none completed. |
| `machines[].lastRunUtc` | string | When its newest job started. |
| `machines[].speedFactor` | number or null | How much longer it takes than is typical for the same command (1.62 means 62% slower, 0.5 twice as fast), from its completed jobs of the last 90 days that ran 30 s or more, clamped to 0.25 to 4, two decimals. `null` under 5 such jobs. |
| `machines[].speedSamples` | number | How many such jobs it has (0 for none). |
| `commands` | array | One entry per command, newest first (by the start of its newest job); at most 500. |
| `commands[].command` | string | The command's key (its arguments; for `--shards` with `{shard}` and `{shards}` kept). A line written before 0.7.0 has no key and groups by its recorded command. |
| `commands[].dir` | string or null | The normalised `--dir`; `null` for lines written before 0.7.0. The same command in another `--dir` is another entry. |
| `commands[].runs` | number | Distinct runs: every shard (and retried attempt) of one `--shards` run is one run. |
| `commands[].jobs` | number | Every job: each shard and each retried attempt is one. |
| `commands[].completed`, `.failed`, `.other`, `.medianSeconds`, `.p90Seconds`, `.lastRunUtc` | | As for a machine. |
| `commands[].lastShards` | number or null | The newest job's shard count N (1 for a single run); `null` for lines written before 0.7.0. |
| `commands[].timing` | object or null | What earlier runs say: `{runsUsed, workMinutes, overheadSeconds, suggestedShards}`, the estimate `--shards auto --target-minutes` uses. stats probes nothing, so a machine without its own speed factor counts as 1. `null` for lines without a key, and until a run has completed with a recorded run time in the last 90 days. |
| `commands[].timing.runsUsed` | number | The runs it comes from (the newest 10 of the last 90 days in which at least half the shards completed). |
| `commands[].timing.workMinutes` | number | The whole command's own run time on a machine of speed factor 1, one decimal. |
| `commands[].timing.overheadSeconds` | number | Per shard: wall-clock minus the command's own run time, one decimal. |
| `commands[].timing.suggestedShards` | number or null | With `lanq stats --target-minutes M`: the shards M needs on a machine of speed factor 1, before any free-slot cap. `null` without `--target-minutes`, or when the per-shard overhead leaves less than a tenth of M for the work. |
| `commandsMore` | number | Conditional: only when there are more than 500 commands; how many more. |
| `nextSteps` | array | `Ok`: `none`. stats never suggests a command. `Rejected`: `fix_command`; `InfraError`: `ask_developer`; `Cancelled`: `none`. |

On `Rejected`, `InfraError` and `Cancelled` the object has the same keys, with zeros, `null` and empty lists. Exit 70
means the job log exists but could not be read (permissions, or another program holds it locked); without `--json`
stats then prints the message on stderr and exits 70 too.

## Error object

`health`, `makeready` (one machine or `--all`) and `wake` print this when they have no normal result:

```json
{"status":"Rejected","exitCode":2,"message":"No config found. Pass --config FILE, set LANQ_CONFIG, or create ...",
 "nextSteps":[{"action":"ask_developer","reason":"the config could not be loaded (No config found. ...); the developer owns machines.yml","safeToRunUnattended":false},
  {"action":"run_doctor","reason":"check this computer's lanq setup","command":"lanq doctor --json","argv":["lanq","doctor","--json"],"safeToRunUnattended":true}]}
```

| Key | Type | Notes |
| --- | --- | --- |
| `status` | string | `Rejected`, `InfraError`, `Held` (makeready only) or `Cancelled`. |
| `exitCode` | number | 2, 70, 75 or 130. |
| `message` | string | One line saying what went wrong. |
| `nextSteps` | array | See [nextSteps](#nextsteps). |

`run`, `run --dry-run`, `init`, `makeready --add`, `doctor` and `stats` never print this shape: their errors use their
own object with the same `status`, `exitCode` and `nextSteps` (and `null`, zeros or empty lists where there is no
value).

## nextSteps

Every object above except the single-machine makeready report carries `nextSteps` (each `lanq health` row and
each `makeready --all` machine has its own), an array of at least one step. It is what
lanq would tell a person next, as data. The same steps are printed for people on stderr as `Next: <reason>: <command>`
lines (`[lanq] Next: ...` inside `lanq run`), before the final status line, which stays last; `none` steps are not
printed, and a command that is not safe to run unattended reads `(for the developer) ...`. stdout is never changed by
them in text mode.

```json
{"action":"wait","reason":"no machine could take the job now; machines may free up; run again waiting up to 120 s",
 "command":"lanq run --json --needs dotnet --wait 120 --no-wake -- dotnet test","argv":["lanq","run","--json","--needs","dotnet","--wait","120","--no-wake","--","dotnet","test"],"safeToRunUnattended":true}
```

| Key | Type | Notes |
| --- | --- | --- |
| `action` | string | What the step asks for (below). |
| `reason` | string | Why, for people and agents. Sanitised. |
| `argv` | array of string | Conditional: present exactly when a command is suggested. The exact arguments, starting with `lanq`, with no shell involved. **Authoritative.** |
| `command` | string | Conditional: the same command as one line for bash and PowerShell, only when every argument can be written safely for both (plain words bare, others in single quotes). Left out when an argument holds a quote or a control character, is empty, or is PowerShell's `--%`. A convenience; when in doubt, run `argv`. |
| `safeToRunUnattended` | boolean | Always there. `true` only for a suggested command a script or agent may run without asking: read-only, or the same run again with no more side effects. `false` for every step without a command. |

`action` is one of:

| `action` | Meaning |
| --- | --- |
| `proceed` | Go ahead: the command does what the dry run said. |
| `retry` | Run the same command again, edited only as `argv` shows. |
| `wait` | Something temporary is in the way. With a command, run it (it waits); without one, try later. |
| `fix_code` | The job itself failed or hung: fix the code. `reason` names the log. |
| `fix_command` | The command line is wrong: fix it. |
| `ask_developer` | Only the developer, or a machine's owner, can fix this. Tell them and stop. |
| `run_doctor` | Run `lanq doctor` (read-only) to find the setup problem. |
| `run_health` | Run `lanq health` (read-only) to see the machines now. |
| `none` | Nothing to do. |

Values may be added in a later version. **Treat an `action` you do not know as `ask_developer`.**

What a suggestion can be, by construction:

- Only `lanq run`, `lanq health`, `lanq doctor` or `lanq makeready`. Never `lanq unlock`, `lanq init`, `lanq wake`,
  `lanq health --wake`, `--force`, `--yes` or `--add`. `--setup` appears only as `lanq makeready --ssh X --os Y
  --setup`, after a worker refused the key or its host key is not in `known_hosts` yet.
- `lanq makeready` (any form) is never safe unattended: it runs a script on someone else's PC, or prints setup for the
  developer and the PC's owner.
- `lanq health ... --json`, `lanq doctor` and `lanq doctor --remote` are read-only and safe. So is your own `lanq run`
  with `--dry-run` added.
- A `lanq doctor` suggestion carries `--json` when the command that produced it was run with `--json` (since 0.7.0):
  a `--json` caller gets `lanq doctor --json` or `lanq doctor --remote --json`, a person reading text gets plain
  `lanq doctor`.
- **A suggestion never does more than the command that produced it.** A retry edits your own command line: the job's
  arguments, `--dir`, `--mem`, `--fetch` and `--config` stay as you typed them. It adds `--no-wake` when the original
  could not wake a machine (`--wait 0`) and never drops `--no-wake`. The `wait` step suggests `--wait 120` only when
  the run waited less, and has no command once the run waited that long. `TimedOut` suggests double the `--timeout`
  (at most 10080). A lost connection on `--shards` suggests `--shard-retries 1` only when no shard may still be running.
  A dry run that would start proceeds with the same command without `--dry-run`.
- `--config FILE` is carried into every suggestion.

What lanq suggests, by result:

| Result | Steps |
| --- | --- |
| run `Completed`, `Cancelled` | `none` |
| run `Failed` | `fix_code` (the reason names the log) |
| run `Rejected` | not allowlisted: `ask_developer` (do not work around it); a secret: `fix_code`; bad arguments or the Windows-arguments rule: `fix_command`; config: `ask_developer` + `run_doctor` |
| run `NoCapacity`, `retryable: true` | `wait` (the same run with `--wait 120`) + `run_health` (`lanq health --needs ... --json`), + `run_doctor` when every machine was unreachable |
| run `NoCapacity`, `retryable: false` | `ask_developer` + `run_health` |
| run `InfraError` | a lost connection with the job not running: `retry`; the job may still be running: `ask_developer` + `run_health`; snapshot or bundle: `run_doctor` + `ask_developer`; prepare: `run_doctor` (`--remote`) + `ask_developer`; the archive could not be built: `fix_code`; otherwise `ask_developer` + `run_doctor` |
| run `TimedOut` | `fix_code` + `retry` with double the `--timeout` |
| run `WindowClosed` | `wait` |
| `--shards` | by the overall status as above; `Failed` lists each failing shard and its log; lost connections: `retry` with `--shard-retries 1` |
| dry run `WouldRun` | `proceed` |
| dry run `NoCapacity`, retryable | `wait` (the same dry run, later) + `run_health` |
| health row | ok: `none`; a hold that passes by itself (paused, hours, load, memory): `wait`, no command; low disk: `ask_developer`; git or tar missing: `ask_developer` + `run_doctor` (`--remote`); unreadable: `run_doctor`; down: `ask_developer` + `run_doctor`; asleep: `ask_developer` (the reason names `lanq wake`, never as a command); with `--needs`, a permanent reason: `ask_developer`; busy for this job: `wait` |
| `makeready --all` | OK or WARN: `none`; NOT READY: `ask_developer` with `lanq makeready <machine>`; held: `wait`; unreachable: `run_doctor` + `ask_developer` |
| `makeready --add` | `Added`: `run_doctor` (`lanq doctor --remote`); NOT READY: `ask_developer`; declined: `none`; a duplicate: `fix_command` |
| `wake` | `Sent`: `run_health`; `Awake`: `none`; `NoAnswer`: `ask_developer` |
| `init` | `Created`: `ask_developer` + `run_doctor` |
| `doctor` | `Ok`: `none`; any `warn` or `fail`: `ask_developer` (points to `checks[].fix`); held machines: also `wait`, no command; config: `ask_developer`; `InfraError`: `retry` (the same doctor with `--json`) |
| `stats` | `Ok`, `Cancelled`: `none`; `InfraError`: `ask_developer`; never a command |

For an agent: run only steps with `safeToRunUnattended: true`, at most once each; for anything else, report the
`reason` to the developer and stop.

## Parsing tips

`--log-tail N` puts the end of the job's log in the object, and `logPath` names the whole file, so you can drop stderr.

bash with `jq`:

```bash
out=$(lanq run --json --log-tail 60 --no-wake --needs dotnet -- dotnet test 2>/dev/null); code=$?
echo "$out" | jq -r '"\(.status) exit=\(.exitCode) on \(.machine // "-"): \(.message)"'
echo "$out" | jq -r '.logTail[]?'                       # the last 60 lines; the whole log is at .logPath

# next steps an agent may run by itself
echo "$out" | jq -c '.nextSteps[] | select(.safeToRunUnattended) | .argv'

# NoCapacity: can waiting help?
echo "$out" | jq -r 'select(.status == "NoCapacity") | "retryable=\(.retryable)", (.excluded[] | "\(.machine) \(.reason) \(.why)")'

# before a heavy run: which machines could take it?
lanq health --needs dotnet --mem 4 --json | jq -r '.[] | "\(.name) \(.eligible) \(.jobSlots) \(.why // "")"'

# without --log-tail: stderr to a uniquely named file, then its tail
log="${TMPDIR:-/tmp}/lanq-$(date +%s)-$$.log"
lanq run --json --no-wake --needs dotnet -- dotnet test 2> "$log"; tail -n 40 "$log"

# health: free slots on usable machines
lanq health --json | jq -r '.[] | select(.state == "ok") | "\(.name) \(.freeSlots) \(.size)"'

# sharded run: the shards that did not pass
echo "$out" | jq -r '.shards[] | select(.status != "Completed") | "\(.shard) \(.machine // "-") \(.status) \(.exitCode)"'

# fetch: what came back, and anything that went wrong
echo "$out" | jq -r '.fetched[]?.path, (.fetchSkipped[]? | "skipped \(.path): \(.reason)"), (.fetchError // empty)'

# makeready --all: machines that need attention
lanq makeready --all --json | jq -r '.machines[]? | select(.verdict != "OK") | "\(.machine) \(.verdict) \(.problem // "")"'

# doctor: the failing checks and their fix (the text lines go to stderr)
lanq doctor --remote --json 2>/dev/null | jq -r '.checks[] | select(.level == "fail") | "\(.machine // "local") \(.name): \(.fix // .detail)"'
lanq doctor --json 2>/dev/null | jq '.checks[] | select(.level=="fail")'

# stats: each command's timing, and the slowest machines
lanq stats --json | jq -r '.commands[] | select(.timing) | "\(.command) [\(.dir)]: \(.timing.workMinutes) min + \(.timing.overheadSeconds) s/shard from \(.timing.runsUsed) runs"'
lanq stats --json | jq -r '.machines[] | select(.speedFactor) | "\(.machine) x\(.speedFactor) (\(.speedSamples) jobs)"'

# dry run: how --target-minutes would pick N
lanq run --dry-run --json --needs node --shards auto --target-minutes 10 -- npx jest --shard={shard}/{shards} 2>/dev/null | jq -r '.shardTiming.why // .shardCountWhy'
```

PowerShell (Windows PowerShell 5.1 or PowerShell 7). Join the output with `Out-String` before `ConvertFrom-Json`, so
the multi-line makeready report parses too:

```powershell
$r = lanq run --json --log-tail 60 --no-wake --needs dotnet -- dotnet test 2>$null | Out-String | ConvertFrom-Json; $code = $LASTEXITCODE
"$($r.status) exit=$($r.exitCode) on $($r.machine): $($r.message)"
$r.logTail                                              # the last 60 lines; the whole log is at $r.logPath

# next steps an agent may run by itself
$r.nextSteps | Where-Object safeToRunUnattended | ForEach-Object { $_.argv -join ' ' }

# NoCapacity: can waiting help?
if ($r.status -eq 'NoCapacity') { "retryable=$($r.retryable)"; $r.excluded | ForEach-Object { "$($_.machine) $($_.reason) $($_.why)" } }

# health: free slots on usable machines
lanq health --json | Out-String | ConvertFrom-Json | Where-Object state -eq 'ok' | ForEach-Object { "$($_.name) $($_.freeSlots) $($_.size)" }

# sharded run: the shards that did not pass
$r.shards | Where-Object status -ne 'Completed' | ForEach-Object { "$($_.shard) $($_.machine) $($_.status) $($_.exitCode)" }

# fetch: what came back, and anything that went wrong
$r.fetched | ForEach-Object path; $r.fetchSkipped | ForEach-Object { "skipped $($_.path): $($_.reason)" }; $r.fetchError

# makeready --all: machines that need attention
(lanq makeready --all --json | Out-String | ConvertFrom-Json).machines | Where-Object verdict -ne 'OK' | ForEach-Object { "$($_.machine) $($_.verdict) $($_.problem)" }

# doctor: the failing checks and their fix (the text lines go to stderr)
(lanq doctor --remote --json 2>$null | Out-String | ConvertFrom-Json).checks | Where-Object level -eq 'fail' | ForEach-Object { "$($_.machine) $($_.name): $($_.fix)" }

# stats: each command's timing
(lanq stats --json | Out-String | ConvertFrom-Json).commands | Where-Object timing | ForEach-Object { "$($_.command): $($_.timing.workMinutes) min + $($_.timing.overheadSeconds) s/shard" }
```

In Windows PowerShell 5.1, `2>` on a native command wraps stderr lines as error records, so a log file written with
`2> $log` gets some extra lines; PowerShell 7 writes plain text. Either way, check `$LASTEXITCODE`, not `$?`.

Check the exit code first, then `status`. If stdout is empty or does not parse, report an infrastructure problem and
do not retry in a loop.
