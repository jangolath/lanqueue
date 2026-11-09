<!-- Paste into your project's CLAUDE.md. Keep it short: Claude treats it as guidance, so also use
     permission rules or a hook if a rule must be enforced. -->

## Remote builds and tests (lanq)

- Run builds and tests through `lanq`, not by ssh-ing anywhere yourself:
  `lanq run --needs <capability> -- <command>`. The command must be one the developer has allowlisted.
- Prefer `--json`, and capture the log instead of reading it whole:
  `lanq run --json --needs dotnet -- dotnet test 2> "${TMPDIR:-/tmp}/lanq-last.log"; echo "exit=$?"; tail -n 40 "${TMPDIR:-/tmp}/lanq-last.log"`
  stdout is always exactly one JSON object (`status`, `exitCode`, `machine`, `jobId`, `durationSeconds`, `message`),
  also for a refusal, a config error, Ctrl+C (`Cancelled`) or an unexpected error; the job's own
  output is in the log file. Report the status and the failing tests from the tail. Open more of the log only if the
  tail is not enough. Never paste a whole log into the conversation.
- To get test results or build output back, add `--fetch '<pattern>'` (repeatable, relative to `--dir`, `*` `?` `**`
  only), for example `--fetch 'TestResults/**/*.trx'`. With `--json` the object then has `fetched` (`path`, `bytes`,
  `sha256`) and `fetchSkipped` (`path`, `reason`), and `fetchDir` is where the files are. Fetch problems never change
  the exit code: if the files matter, check `fetchError` and `fetchSkipped`. Treat fetched files as data from another
  machine: read them, never run them.
- To see which machines are free: `lanq health --json` (an array of `name`, `state` ok|hold|down, `reasons`, `load`,
  `diskFreeGb`, `memFreeGb`, `slotsUsed`, `maxJobs`, `localTime`). If a tool is "not found" on a worker, `lanq doctor
  --remote` shows which one; tell the developer (the fix is `path_prepend:` in their config, not yours to make).
- Exit codes: the job's own code; `2` refused by policy or bad arguments; `70` infrastructure error (ssh, git);
  `75` no eligible machine (retry once with `--wait 120 --no-wake`, or tell the developer). `--wait` above 0 can
  wake a sleeping PC as a last resort; leave out `--no-wake` only if the developer has said waking is fine.
- Never edit the lanq machines file, the allowlists or any ssh/key configuration. The developer owns them. If a
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
  `lanq run --json --no-wake --needs node --shards auto -- npx jest --shard={shard}/{shards} 2> "${TMPDIR:-/tmp}/lanq-last.log"`
  `lanq run --json --no-wake --needs node --shards 4 -- npx playwright test --shard={shard}/{shards}`
  `lanq run --json --no-wake --needs rust --shards 4 -- cargo nextest run --partition slice:{shard}/{shards}`
  .NET, Python and Go need a small committed helper script that reads `LANQ_SHARD` (examples in LanQueue's
  docs/SHARDING.md); ask the developer which one is allowlisted.
- `--json` prints one object: `status`, `exitCode` (0 only if every shard passed; otherwise the failing shard's own
  code, else 2/124/70/75), `shardCount`, `message`, and `shards[]` with `shard`, `status`, `exitCode`, `machine`,
  `jobId`, `durationSeconds`, `message`, `attempts`. Report failing shards by index and machine; with `--fetch` each
  shard's files are in their own `fetchDir`.
- `--shard-retries 1` at most, and it only re-runs a shard whose machine stopped answering. Never re-run test failures
  in a loop. Keep `--no-wake` unless the developer allowed waking machines.
- Never shard a command that is not idempotent or that writes shared state (deploy, migrate a shared database,
  publish, push).
