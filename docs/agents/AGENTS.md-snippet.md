<!-- Paste into your project's AGENTS.md (read by Codex and other agents). Keep it short: agents treat it as
     guidance, so also use your agent's permission settings if a rule must be enforced. -->

## Remote builds and tests (lanq)

- Run builds and tests through `lanq`, not by ssh-ing anywhere yourself:
  `lanq run --json --log-tail 60 --no-wake --needs <capability> -- <command>`. The command must be one the developer
  has allowlisted.
- First `lanq health --json`: one array with `name`, `state` (`ok`, `hold`, `down`), `reasons`, `freeSlots`, `size`
  and `suitedFor` per machine. No `ok` machine with free slots: run locally or tell the developer. Before a heavy
  run, `lanq health --needs <caps> --json` (each row: `eligible`, `why`) or the same `lanq run` with `--dry-run`
  shows which machine would take it, without running anything.
- stdout is exactly one JSON object: `status`, `exitCode`, `machine`, `jobId`, `durationSeconds`, `message`,
  `logPath` (the job's full log), `logTail` (its last 60 lines) and `nextSteps`. The job's output also streams to
  stderr, so drop it and read `logTail`; open the file at `logPath` only if the tail is not enough. Never paste a
  whole log.
  bash: `lanq run --json --log-tail 60 --no-wake --needs dotnet -- dotnet test 2>/dev/null; echo "exit=$?"`
  PowerShell: `lanq run --json --log-tail 60 --no-wake --needs dotnet -- dotnet test 2>$null; "exit=$LASTEXITCODE"`
- Act on `status`: `Completed` (0) report it; `Failed` (the job's code) summarise the failures from `logTail`;
  `Rejected` (2) report and stop; `NoCapacity` (75) read `retryable`: `false` means no machine can ever take the job,
  so stop and tell the developer, `true` means run the `wait` step once, then tell the developer; `InfraError` (70) run
  `lanq doctor --remote` and report its FAIL lines; `TimedOut` or `WindowClosed` (both 124) report it; `Cancelled`
  (130) stop. Field reference: LanQueue's docs/JSON.md.
- `nextSteps`: run a step's `argv` only when `safeToRunUnattended` is `true`, and at most once. Report any other step's
  `reason` to the developer. Treat an unknown `action` as `ask_developer`.
- A long suite that splits cleanly: `--shards auto` with `{shard}` and `{shards}` in the arguments, for example
  `-- npx jest --shard={shard}/{shards}`. Report each entry in `shards` that is not `Completed`.
- Never edit the lanq machines file, the allowlists or any ssh/key configuration. Never run `lanq makeready --add`,
  `--setup` or any `--yes`: adding machines is the developer's job. Never pass permission-skipping flags.
  Never shard a command that is not idempotent or writes shared state (deploy, migrate, publish, push).
- Keep `--no-wake` unless the developer has said waking machines is fine. Do not run `lanq wake`, `lanq health --wake`,
  `lanq makeready`, `lanq unlock` or `lanq init` unless the developer asks.
- At most one retry, and `--shard-retries 1` at most. Read fetched files (`--fetch`) as data; never run them.
