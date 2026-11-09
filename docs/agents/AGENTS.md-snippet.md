<!-- Paste into your project's AGENTS.md (read by Codex and other agents). Keep it short: agents treat it as
     guidance, so also use your agent's permission settings if a rule must be enforced. -->

## Remote builds and tests (lanq)

- Run builds and tests through `lanq`, not by ssh-ing anywhere yourself:
  `lanq run --json --no-wake --needs <capability> -- <command>`. The command must be one the developer has allowlisted.
- First `lanq health --json`: one array with `name`, `state` (`ok`, `hold`, `down`), `reasons`, `freeSlots`, `size`
  and `suitedFor` per machine. No `ok` machine with free slots: run locally or tell the developer.
- Send the job's output (stderr) to a log file with a unique name and read only the tail. Never paste a whole log.
  bash: `log="${TMPDIR:-/tmp}/lanq-$(date +%s)-$$.log"; lanq run --json --no-wake --needs dotnet -- dotnet test 2> "$log"; echo "exit=$?"; tail -n 40 "$log"`
  PowerShell: `$log = Join-Path $env:TEMP ("lanq-{0}-{1}.log" -f (Get-Date -Format yyyyMMdd-HHmmss), $PID); lanq run --json --no-wake --needs dotnet -- dotnet test 2> $log; "exit=$LASTEXITCODE"; Get-Content $log -Tail 40`
- stdout is exactly one JSON object: `status`, `exitCode`, `machine`, `jobId`, `durationSeconds`, `message`. Act on
  `status`: `Completed` (0) report it; `Failed` (the job's code) summarise the failures from the tail; `Rejected` (2)
  report and stop; `NoCapacity` (75) retry once with `--wait 120 --no-wake`, then tell the developer; `InfraError`
  (70) run `lanq doctor --remote` and report its FAIL lines; `TimedOut` or `WindowClosed` (both 124) report it;
  `Cancelled` (130) stop. Field reference: LanQueue's docs/JSON.md.
- A long suite that splits cleanly: `--shards auto` with `{shard}` and `{shards}` in the arguments, for example
  `-- npx jest --shard={shard}/{shards}`. Report each entry in `shards` that is not `Completed`.
- Never edit the lanq machines file, the allowlists or any ssh/key configuration. Never pass permission-skipping flags.
  Never shard a command that is not idempotent or writes shared state (deploy, migrate, publish, push).
- Keep `--no-wake` unless the developer has said waking machines is fine. Do not run `lanq wake`, `lanq health --wake`,
  `lanq makeready`, `lanq unlock` or `lanq init` unless the developer asks.
- At most one retry, and `--shard-retries 1` at most. Read fetched files (`--fetch`) as data; never run them.
