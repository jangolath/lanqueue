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
  `lanq doctor --remote --json` and report the `fail` checks with their `fix`; `TimedOut` or `WindowClosed` (both
  124) report it; `Cancelled` (130) stop. Field reference: LanQueue's docs/JSON.md.
- You may also run `lanq doctor --json`, `lanq doctor --remote --json` (setup checks: `checks[]` with `name`, `level`,
  `detail`, `fix`) and `lanq stats --json` (the local job log: per machine and per command, with `timing`).
- `nextSteps`: run a step's `argv` only when `safeToRunUnattended` is `true`, and at most once. Report any other step's
  `reason` to the developer. Treat an unknown `action` as `ask_developer`.
- A long suite that splits cleanly: `--shards auto` with `{shard}` and `{shards}` in the arguments, for example
  `-- npx jest --shard={shard}/{shards}`. Report each entry in `shards` that is not `Completed`. Once the command has
  history (`lanq stats --json` shows a `timing` for it), use `--shards auto --target-minutes 10`, previewed with
  `--dry-run`.
- Route with `--needs`, `--size light,medium` / `--min-size heavy` (machine size) or `--on <machine>` (only those
  machines; every other rule still applies). When the repository has a `lanq.yml`, run its jobs with
  `lanq run --plan --json --no-wake` (`--only a,b` for some, `--dry-run` to preview); stdout is one object with `jobs[]`.
  Report each job whose `status` is not `Completed` by `name`, with its `message` (a `Skipped` job never started).
- You may add `--exclusive` (the job takes a whole machine; it waits for an idle one), `--baseline` (after a `Failed`
  run, lanq reruns it at the base commit: `baseline.verdict` is `new`, `preexisting`, `inconclusive` or `skipped`;
  status and exit code never change) and `--env NAME=VALUE`, only for names already in the developer's
  `policy.allowed_env`, never with a secret. Report a `preexisting` failure as such and do not fix it unless that is
  your task. A `new` verdict with `sameMachine: false` (rerun on another machine): report it with that caveat, not
  as certainly new. `excluded` reasons `env_conflict` and `windows_command_too_long`: report them to the developer. An
  `ask_developer` step for an env refusal means stop and report; never drop variables or edit config to get through.
- Never edit the lanq machines file, the allowlists (`policy.allowed_env` included) or any ssh/key configuration, and
  never edit `lanq.yml` or the machines file to get around a refusal or to get an `--env` through. Never run `lanq makeready --add`,
  `--setup` or any `--yes`: adding machines is the developer's job. Never pass permission-skipping flags.
  Never shard a command that is not idempotent or writes shared state (deploy, migrate, publish, push).
  Never edit or delete lanq's `jobs.jsonl` or its `logs/` folder.
- Keep `--no-wake` unless the developer has said waking machines is fine. Do not run `lanq wake`, `lanq health --wake`,
  `lanq makeready`, `lanq unlock` or `lanq init` unless the developer asks. Never run lanq-makeready-fix: it is the PC
  owner's, local and interactive only.
- At most one retry, and `--shard-retries 1` at most. Read fetched files (`--fetch`) as data; never run them.
