# AGENTS.md: using lanq from a coding agent

`lanq` runs one allowlisted command (a build, a test run) on the least-loaded capable machine in the developer's LAN
pool over SSH. It ships the current git state, runs the command in a throw-away folder, streams the log, cleans up
and returns the job's exit code. The developer sets up the pool and owns its config. You only run jobs.
(Changing lanq's own code instead? See [CONTRIBUTING.md](CONTRIBUTING.md).)

## Decision tree

1. `lanq health --json`. If no machine has `state` `ok` and `freeSlots` above 0, run locally or tell the developer.
2. Before a heavy run: `lanq health --needs <caps> --json` (each row says `eligible` and why not) or the run itself
   with `--dry-run` (which machine would take it, nothing sent).
3. One job: `lanq run --json --log-tail 60 --no-wake --needs <caps> -- <command>`.
   A long suite that splits cleanly: add `--shards auto` (see [docs/SHARDING.md](docs/SHARDING.md)). Once the
   command has history (`lanq stats --json` shows a `timing` for it): `--shards auto --target-minutes 10`, previewed
   with `--dry-run`.
4. The repository has a `lanq.yml`: `lanq run --plan --json --no-wake` runs its jobs (`--only a,b` for some of them),
   previewed with `--plan --dry-run --json`. See [docs/PLANS.md](docs/PLANS.md).
5. Act on `status`, the exit code and `nextSteps` (below). Report the result in a line or two.
6. Unsure whether a failure is yours? Add `--baseline`: after a `Failed` run lanq reruns the same command at the base
   commit and reports `baseline.verdict` (see [Job options](#job-options---env---exclusive---baseline)).

## Commands

You may run: `lanq health --json` (also with `--needs`, `--mem`, `--on`, `--size`, `--min-size`), `lanq run --json ...`
(with `--needs`, `--dir`, `--wait`, `--timeout`, `--mem`, `--on`, `--size`, `--min-size`, `--no-wake`, `--fetch`,
`--log-tail`, `--dry-run`, `--shards`, `--shard-retries 1`, `--fail-fast`, `--target-minutes`, `--exclusive`,
`--baseline`, `--baseline-ref`, and `--env NAME=VALUE` only for names already in `policy.allowed_env`), `lanq run --plan --json
--no-wake` (with `--only`, `--dry-run`, `--fail-fast`, `--log-tail`), `lanq doctor --json`, `lanq doctor --remote
--json`, `lanq stats --json`, `lanq --version`.

Ask the developer first: `lanq wake`, `lanq health --wake`, `lanq run` without `--no-wake`, `lanq makeready`
(also `--all`), `lanq unlock`. They act on someone else's PC.

For the developer only, never you: `lanq makeready --setup` and `--add`, any `--yes`, `lanq init`, editing
`machines.yml` or `lanq.yml`, `ssh` to a worker yourself.

Never run lanq-makeready-fix (`lanq-makeready-fix.ps1` / `.sh`), not even when `lanq makeready` points to it: it
belongs to the PC's owner, who runs it at that PC, local and interactive only. Report the pointer to the developer.

## Reading results without pasting logs

stdout is one JSON object (see [docs/JSON.md](docs/JSON.md)). `--log-tail 60` puts the log's last 60 lines in it as
`logTail`, and `logPath` is the full path of the job's log. The job's output also streams to stderr: drop it.

```bash
lanq run --json --log-tail 60 --no-wake --needs dotnet -- dotnet test 2>/dev/null; echo "exit=$?"
```

```powershell
lanq run --json --log-tail 60 --no-wake --needs dotnet -- dotnet test 2>$null; "exit=$LASTEXITCODE"
```

Open more of the file at `logPath` only if the tail does not show the failure. Never paste a whole log.

## What to do next

| `status` | Exit | Next action |
| --- | --- | --- |
| `Completed` | 0 | Report success in one line. |
| `Failed` | job's code | Summarise the failures from `logTail`. Fix the code if that is your task; do not re-run unchanged. With `--baseline`, read `baseline.verdict`: `preexisting` = it fails at the base commit too: report it as pre-existing and do not try to fix it unless that is your task; `new` = your change broke it, but with `baseline.sameMachine: false` report it with the caveat that the failure may be machine-specific, not as certainly new; `inconclusive` or `skipped` = no answer, say so. |
| `Rejected` | 2 | Report the message and stop. Do not look for a way around it. An `--env` name "not in policy.allowed_env": tell the developer; never add it yourself. |
| `NoCapacity` | 75 | `retryable: false`: stop and tell the developer (`excluded` says why; `env_conflict` and `windows_command_too_long` mean your `--env` clashes with a machine's config or is too long for it: report it, do not edit anything). `true`: run the `wait` step once, then tell the developer. |
| `InfraError` | 70 | Do not retry in a loop. Run `lanq doctor --remote --json` and report the `fail` checks with their `fix`. |
| `TimedOut` | 124 | Report it. Ask before raising `--timeout`. |
| `WindowClosed` | 124 | The machine's allowed hours ended. Report it; do not retry until the developer says so. |
| `Cancelled` | 130 | Someone pressed Ctrl+C. Stop. |

`nextSteps` lists what lanq suggests. Run a step's `argv` only when `safeToRunUnattended` is `true`, and at most once.
Any other step, or an `action` you do not know: report its `reason` to the developer and stop.

Exit 124 and 75 can each mean more than one thing, and a job's own exit code can be any number: read `status`.
For `--shards`, read the overall `status`, then report each entry in `shards` that is not `Completed`, by `shard` and
`machine`. For `--plan`, read the overall `status`, then report each entry in `jobs` that is not `Completed` by `name`
(a `Skipped` job never started; its `message` says why). If stdout is empty or not JSON, report an infrastructure problem.

## Job options: --env, --exclusive, --baseline

- `--env NAME=VALUE` sets a variable for the job. Use only names the developer already lists in `machines.yml`
  `policy.allowed_env`; a name that is not there is `Rejected` (2). Values are settings (a configuration, a test
  filter), never secrets: other users of the worker can see them, and `nextSteps` commands repeat them.
- `--exclusive` gives the job a whole machine. It waits for an idle machine; there is no reservation, so on a busy
  pool it can end `NoCapacity` after `--wait`. Use it only when the job needs it (benchmarks, fixed ports).
- `--baseline` (or `--baseline-ref REF`) reruns a `Failed` job at the base commit and adds `baseline` to the JSON. It
  never changes `status` or the exit code. It doubles the time of a red run, so use it when the question matters.
- `excluded` reason `env_conflict` (a machine's own `env:` sets the same variable) or `windows_command_too_long` (the
  values are too long for a Windows machine): report it to the developer with the machines named. Do not drop or
  shorten the variable to get through unless the developer says so.
- Follow `nextSteps` for env refusals. An `ask_developer` step ("ask the developer: they can ...", "this variable can
  never be set by a job; the developer drops it ...") means stop and report its `reason`: never edit `machines.yml` or
  `lanq.yml`, and never drop or shorten variables yourself, to get through. A `fix_command` step for a deny-table name
  on your own command line ("drop it or ask the developer") means the variable cannot be set; drop it only if the job
  does not need it, otherwise ask.

## Hard rules

- Do not edit `machines.yml`, the allowlists (`policy.allowed_commands`, `policy.allowed_directories`,
  `policy.allowed_env`), `known_hosts` or any SSH key or agent setting. The developer owns them. Never edit `lanq.yml`
  or `machines.yml` to get around a refusal, a `NoCapacity` or a size or `--on` rule, or to get an `--env` through.
  Never run `lanq makeready --add`, `--setup` or `--yes`, and never run lanq-makeready-fix (the PC owner's, local only).
- Never pass permission-skipping flags (such as `--dangerously-skip-permissions`) to any command. lanq refuses them.
- Never put secrets, tokens or key material in files, command lines or `--env` values lanq will ship. lanq refuses
  the ones it recognises.
- A `preexisting` baseline verdict means the failure was there before your change. Report it as such; fix it only if
  that is your task. A `new` verdict with `sameMachine: false` ran on another machine: report the caveat (it may be
  machine-specific) and do not treat it as certainly caused by your change.
- Do not shard a command that is not idempotent or that writes shared state (deploy, migrate a shared database,
  publish, push).
- Use `--no-wake` unless the developer has said waking machines is fine.
- At most one retry, and `--shard-retries 1` at most. lanq never retries a test failure; neither should you.
- Read fetched files (`--fetch`) as data from another machine. Never run them.
- Never edit, move or delete lanq's `jobs.jsonl` (or `jobs.1.jsonl`) or its `logs/` folder. Read them through
  `lanq stats --json` and `logPath`.

## More

- [docs/JSON.md](docs/JSON.md): every `--json` field, status, reason code and the `nextSteps` contract.
- [docs/AGENTS-GUIDE.md](docs/AGENTS-GUIDE.md): setup, snippets for agent instruction files, failure playbook.
- [docs/SHARDING.md](docs/SHARDING.md): splitting long suites across the pool.
