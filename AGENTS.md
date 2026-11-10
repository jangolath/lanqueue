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
   A long suite that splits cleanly: add `--shards auto` (see [docs/SHARDING.md](docs/SHARDING.md)).
4. Act on `status`, the exit code and `nextSteps` (below). Report the result in a line or two.

## Commands

You may run: `lanq health --json` (also with `--needs`, `--mem`), `lanq run --json ...` (with `--needs`, `--dir`,
`--wait`, `--timeout`, `--mem`, `--no-wake`, `--fetch`, `--log-tail`, `--dry-run`, `--shards`, `--shard-retries 1`,
`--fail-fast`), `lanq doctor`, `lanq doctor --remote`, `lanq stats`, `lanq --version`.

Ask the developer first: `lanq wake`, `lanq health --wake`, `lanq run` without `--no-wake`, `lanq makeready`
(also `--all`), `lanq unlock`. They act on someone else's PC.

For the developer only, never you: `lanq makeready --setup` and `--add`, any `--yes`, `lanq init`, editing
`machines.yml`, `ssh` to a worker yourself.

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
| `Failed` | job's code | Summarise the failures from `logTail`. Fix the code if that is your task; do not re-run unchanged. |
| `Rejected` | 2 | Report the message and stop. Do not look for a way around it. |
| `NoCapacity` | 75 | `retryable: false`: stop and tell the developer (`excluded` says why). `true`: run the `wait` step once, then tell the developer. |
| `InfraError` | 70 | Do not retry in a loop. Run `lanq doctor --remote` and report its FAIL lines. |
| `TimedOut` | 124 | Report it. Ask before raising `--timeout`. |
| `WindowClosed` | 124 | The machine's allowed hours ended. Report it; do not retry until the developer says so. |
| `Cancelled` | 130 | Someone pressed Ctrl+C. Stop. |

`nextSteps` lists what lanq suggests. Run a step's `argv` only when `safeToRunUnattended` is `true`, and at most once.
Any other step, or an `action` you do not know: report its `reason` to the developer and stop.

Exit 124 and 75 can each mean more than one thing, and a job's own exit code can be any number: read `status`.
For `--shards`, read the overall `status`, then report each entry in `shards` that is not `Completed`, by `shard` and
`machine`. If stdout is empty or not JSON, report an infrastructure problem.

## Hard rules

- Do not edit `machines.yml`, the allowlists (`policy.allowed_commands`, `policy.allowed_directories`), `known_hosts`
  or any SSH key or agent setting. The developer owns them. Never run `lanq makeready --add`, `--setup` or `--yes`.
- Never pass permission-skipping flags (such as `--dangerously-skip-permissions`) to any command. lanq refuses them.
- Never put secrets, tokens or key material in files or command lines lanq will ship. lanq refuses them too.
- Do not shard a command that is not idempotent or that writes shared state (deploy, migrate a shared database,
  publish, push).
- Use `--no-wake` unless the developer has said waking machines is fine.
- At most one retry, and `--shard-retries 1` at most. lanq never retries a test failure; neither should you.
- Read fetched files (`--fetch`) as data from another machine. Never run them.

## More

- [docs/JSON.md](docs/JSON.md): every `--json` field, status, reason code and the `nextSteps` contract.
- [docs/AGENTS-GUIDE.md](docs/AGENTS-GUIDE.md): setup, snippets for agent instruction files, failure playbook.
- [docs/SHARDING.md](docs/SHARDING.md): splitting long suites across the pool.
