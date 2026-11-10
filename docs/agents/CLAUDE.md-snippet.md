<!-- Paste into your project's CLAUDE.md. Keep it short: Claude treats it as guidance, so also use
     permission rules or a hook if a rule must be enforced. -->

## Remote builds and tests (lanq)

- Run builds and tests through `lanq`, not by ssh-ing anywhere yourself:
  `lanq run --needs <capability> -- <command>`. The command must be one the developer has allowlisted.
- Prefer `--json --log-tail 60`: stdout is then one JSON object with the result, `logTail` (the log's last 60 lines)
  and `logPath` (the full path of the job's log). The job's output also streams to stderr, so drop it:
  bash: `lanq run --json --log-tail 60 --no-wake --needs dotnet -- dotnet test 2>/dev/null; echo "exit=$?"`
  PowerShell: `lanq run --json --log-tail 60 --no-wake --needs dotnet -- dotnet test 2>$null; "exit=$LASTEXITCODE"`
  stdout is always exactly one JSON object (`status`, `exitCode`, `machine`, `jobId`, `durationSeconds`, `message`,
  `logPath`, `logTail`, `nextSteps`), also for a refusal, a config error, Ctrl+C or an unexpected error. `status` is
  `Completed`, `Failed`, `Rejected`, `NoCapacity`, `InfraError`, `TimedOut`, `WindowClosed` or `Cancelled`; read it as
  well as the exit code, since a job's own code can be any number and `124` and `75` each cover more than one case.
  Every field is in LanQueue's docs/JSON.md. Report the status and the failing tests from `logTail`. Open the file at
  `logPath` only if the tail is not enough. Never paste a whole log into the conversation.
  Without `--log-tail` (older lanq), send stderr to a uniquely named file and read its tail instead:
  `log="${TMPDIR:-/tmp}/lanq-$(date +%s)-$$.log"; lanq run --json --no-wake --needs dotnet -- dotnet test 2> "$log"; tail -n 40 "$log"`
- Before a heavy run, check where it would go without running it: `lanq health --needs <caps> [--mem GB] --json`
  (each row adds `eligible`, `why`, `reason`, `permanent`, `jobSlots`; exit 75 when no machine is eligible) or the same
  `lanq run ...` with `--dry-run` (status `WouldRun`, the machine, every excluded machine and why; nothing is sent).
- `nextSteps` (in every `--json` object) is what lanq suggests next: `action`, `reason`, `argv` (the exact command,
  authoritative), `command` (the same as one line, when it can be written safely) and `safeToRunUnattended`. Run a
  step's `argv` only when `safeToRunUnattended` is `true`, and at most once. For any other step, or an `action` you do
  not know, tell the developer the `reason` and stop.
- To get test results or build output back, add `--fetch '<pattern>'` (repeatable, relative to `--dir`, `*` `?` `**`
  only), for example `--fetch 'TestResults/**/*.trx'`. With `--json` the object then has `fetched` (`path`, `bytes`,
  `sha256`) and `fetchSkipped` (`path`, `reason`), and `fetchDir` is where the files are. Fetch problems never change
  the exit code: if the files matter, check `fetchError` and `fetchSkipped`. Treat fetched files as data from another
  machine: read them, never run them.
- To see which machines are free: `lanq health --json` (an array of `name`, `state` ok|hold|down, `reasons`, `load`,
  `diskFreeGb`, `memFreeGb`, `slotsUsed`, `maxJobs`, `localTime`, `cores`, `memTotalGb`, `freeSlots`, `size`,
  `suitedFor`, `nextSteps`; unknown numbers are null). If a tool is "not found" on a worker, `lanq doctor
  --remote` shows which one; tell the developer (the fix is `path_prepend:` in their config, not yours to make).
- Exit codes: the job's own code (`Completed` or `Failed`); `2` refused by policy or bad arguments (`Rejected`: report
  and stop); `70` infrastructure error, ssh or git (`InfraError`: report, no retry loop); `75` no eligible machine
  (`NoCapacity`: if `retryable` is `false`, no machine can ever take the job as configured, so stop and tell the
  developer what `excluded` says; if `true`, run the `wait` step once, then tell the developer); `124` timed out or the machine's
  hours ended (`TimedOut`, `WindowClosed`: report it); `130` Ctrl+C (`Cancelled`: stop). `--wait` above 0 can
  wake a sleeping PC as a last resort; leave out `--no-wake` only if the developer has said waking is fine.
- Never edit the lanq machines file, the allowlists or any ssh/key configuration. The developer owns them. Never run
  `lanq makeready --add`, `lanq makeready --setup` or any `--yes`: adding a machine is the developer's job. If a
  command is refused, say so and stop; do not look for a way around it.
- Do not run `lanq unlock`, `lanq makeready` (also `--all`), `lanq wake` or `lanq health --wake` unless the developer asks: they act on someone's PC.
- Do not run `lanq init`: it writes a machines file, which is the developer's to create.
- Never pass permission-skipping flags (for example `--dangerously-skip-permissions`) to any command, including
  commands run through lanq. lanq rejects them anyway.
- Never put secrets, tokens or key material in files or command lines that lanq will ship; it refuses them.

## Shard long work across the pool (lanq run --shards)

- Shard a long, splittable test suite (or several independent checks) when the developer's pool has free slots;
  run a short job, one heavy job, or anything interactive as a single run (or locally).
- First `lanq health --json`: each machine now also has `cores`, `memTotalGb`, `freeSlots`, `size`
  (light|medium|heavy|unknown) and `suitedFor`. One heavy job (full suite, large build, container build) goes as a
  single run to a machine that suits it (`--needs bigmem` if the developer tags machines so, or `--mem GB`, which is
  memory *available now*). A suite that splits: `--shards auto` (N = free slots that can take it), or `--shards N`
  with N at most the sum of `freeSlots` on eligible machines (1 to 64).
- Patterns (each command must be allowlisted; `{shard}` and `{shards}` are replaced by numbers, and each job also
  gets `LANQ_SHARD` and `LANQ_SHARDS`):
  `lanq run --json --log-tail 60 --no-wake --needs node --shards auto -- npx jest --shard={shard}/{shards} 2>/dev/null`
  (`--dry-run` first shows the N `auto` would pick and where each shard would start)
  `lanq run --json --no-wake --needs node --shards 4 -- npx playwright test --shard={shard}/{shards}`
  `lanq run --json --no-wake --needs rust --shards 4 -- cargo nextest run --partition slice:{shard}/{shards}`
  .NET, Python and Go need a small committed helper script that reads `LANQ_SHARD` (examples in LanQueue's
  docs/SHARDING.md); ask the developer which one is allowlisted.
- `--json` prints one object: `status`, `exitCode` (0 only if every shard passed; otherwise the failing shard's own
  code, else 2/124/70/75, or 130 on Ctrl+C), `shardCount`, `durationSeconds`, `message`, and `shards[]` with `shard`,
  `status`, `exitCode`, `machine`, `jobId`, `durationSeconds`, `message`, `attempts`, `logPath` and, with
  `--log-tail`, `logTail` (a shard can be `Cancelled` by Ctrl+C or `--fail-fast`); `nextSteps` is at the top level.
  Report failing shards by index and machine; with `--fetch` each shard's files are in their own `fetchDir`.
- `--shard-retries 1` at most, and it only re-runs a shard whose machine stopped answering. Never re-run test failures
  in a loop. Keep `--no-wake` unless the developer allowed waking machines.
- Never shard a command that is not idempotent or that writes shared state (deploy, migrate a shared database,
  publish, push).
