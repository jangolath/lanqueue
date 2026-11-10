# Using lanq from an AI coding agent

Coding agents are good at deciding what to build and test. They waste context working out how to reach a remote
box: composing ssh commands, retrying, pasting long logs back. With `lanq` the agent runs one command, reads one JSON
object (with a short log tail and the next steps in it), and moves on. The remote mechanics, the allowlist and the
cleanup live in lanq.

This page is for the developer who sets it up. The short rules an agent reads are in [AGENTS.md](../AGENTS.md); every
JSON field is in [JSON.md](JSON.md).

## Who owns what

| | The developer | The agent |
| --- | --- | --- |
| `machines.yml`, `policy.allowed_commands`, `policy.allowed_directories` | writes and owns | never edits |
| SSH keys, ssh-agent, `known_hosts` | sets up | never touches |
| Workers (OpenSSH, tools, `PAUSE` files) | sets up, or asks each PC's owner | never touches |
| `lanq makeready --setup`, `lanq makeready --add` (and its `--yes`), `lanq init` | runs | never |
| `lanq makeready`, `lanq unlock`, `lanq wake`, `lanq health --wake` | runs | only when asked |
| `lanq health --json` (also `--needs`), `lanq run --json` (also `--dry-run`), `lanq doctor --json` (also `--remote`), `lanq stats --json` | runs | runs |
| `jobs.jsonl`, `jobs.1.jsonl` and `logs/` in lanq's state folder | owns (lanq writes them) | never touches (reads them through `lanq stats --json` and `logPath`) |
| `nextSteps` with `safeToRunUnattended: true` | | runs, at most once |

The hard stops are in lanq, not in the agent's instructions: it refuses commands outside your allowlist,
permission-skipping flags and secrets in the payload. Text in an instruction file is guidance the model follows. To
also keep the agent away from the config file, use your agent's permission settings.

## Setup

1. Set up the pool: [TEAM-QUICKSTART.md](TEAM-QUICKSTART.md) for several machines, or the README's
   [Quick start](../README.md#quick-start) for one.
2. Allowlist only what the agent should run, for example `dotnet test`, `npm test`, `npx jest`.
3. Put `machines.yml` at the per-user path, or set `LANQ_CONFIG` in the environment the agent's shell inherits. The
   agent should not need `--config`.
4. Check it yourself first: `lanq doctor --remote`, then `lanq run --json --no-wake --needs <cap> -- <command>`.
5. Add one of the snippets below to the project, so the agent knows lanq is there and how to use it.

## Snippets

- **Claude Code (`CLAUDE.md`)**: paste [agents/CLAUDE.md-snippet.md](agents/CLAUDE.md-snippet.md), or copy the
  [agents/skills/lanq](agents/skills/lanq/SKILL.md) folder to `.claude/skills/` in your project.
- **`AGENTS.md`** (read by Codex and several other agents): paste [agents/AGENTS.md-snippet.md](agents/AGENTS.md-snippet.md)
  into your project's `AGENTS.md`.
- **Any other agent**: put this in its system prompt or rules file.

```text
You can run builds and tests on the developer's LAN machines with the lanq CLI.
- Check first: lanq health --json (one array; use machines with state "ok" and freeSlots > 0).
  Before a heavy run: lanq health --needs <caps> --json, or the run with --dry-run (nothing is sent).
- Run: lanq run --json --log-tail 60 --no-wake --needs <capability> -- <allowlisted command> 2>/dev/null
  The JSON has logTail (the last 60 lines) and logPath (the whole log). Never paste a whole log.
- stdout is one JSON object. Act on "status": Completed = passed; Failed = summarise the failures from logTail;
  Rejected = report and stop; NoCapacity = if retryable is false, stop and ask; if true, run the wait step once,
  then ask; InfraError = run lanq doctor --remote --json and report the "fail" checks with their "fix";
  TimedOut / WindowClosed = report; Cancelled = stop.
- nextSteps: run a step's argv only if safeToRunUnattended is true, at most once. Otherwise report its reason.
  An unknown action means ask the developer.
- Never edit lanq's machines.yml, allowlists or SSH keys. Never pass permission-skipping flags.
- Never run lanq makeready --setup, lanq makeready --add, any --yes, or lanq init.
- Do not run lanq wake, lanq health --wake, lanq makeready, lanq unlock unless asked.
- Never shard a command that is not idempotent. Retry at most once.
- Long suites: --shards auto --target-minutes 10 once lanq stats --json shows a "timing" for the command; preview
  with --dry-run.
- Never edit or delete lanq's jobs.jsonl or logs/ folder.
```

## The standard loop

1. `lanq health --json`. Pick capabilities and, for a heavy job, a `size` that suits it (`--needs bigmem` if you tag
   machines that way, or `--mem GB`). `lanq health --needs <caps> [--mem GB] --json` says per machine whether it
   could take such a job now (`eligible`, `why`, `jobSlots`); `lanq run ... --dry-run --json` says which machine the
   run would take, and why the others are out, without sending anything.
2. `lanq run --json --log-tail 60 --no-wake --needs <caps> [--dir D] [--fetch PATTERN] -- <command>`, with stderr
   dropped (the log is in `logTail` and at `logPath`). A long suite that splits: `--shards auto` (see
   [SHARDING.md](SHARDING.md)); once `lanq stats --json` shows a `timing` for the command,
   `--shards auto --target-minutes 10`, previewed with `--dry-run`.
3. Read the exit code, then `status`, then `nextSteps`. Read `logTail` only to explain a failure.
4. Report in a line or two: status, machine, and the failing tests or the message.

## Failure playbook

| `status` | Exit | Likely cause | What the agent does | What to ask the developer |
| --- | --- | --- | --- | --- |
| `Failed` | job's code | The tests or build failed. | Summarise the failures from the tail; fix the code if that is the task. | Nothing, unless the failure looks environmental (a missing tool, a full disk). |
| `Rejected` | 2 | Not on the allowlist, a bad flag, a permission-skipping flag, a secret in the payload, a config error. | Report the message. Stop. | Whether to allowlist the command or fix the config. |
| `NoCapacity`, `retryable: true` | 75 | Every machine busy, held (hours, pause, memory, load), asleep or unreachable. | Run the `wait` step from `nextSteps` once (the same run with `--wait 120`). | Whether to wait, wake a machine, or run locally. |
| `NoCapacity`, `retryable: false` | 75 | No machine can ever take the job as configured: none has a `--needs` tag, `--mem` is above every machine's RAM, git or tar is missing. lanq ended at once. | Stop. Report `excluded`. | Whether to add the capability or a machine, or run locally. |
| `InfraError` | 70 | ssh or git trouble, a machine that stopped answering, a broken snapshot. | Run `lanq doctor --remote --json`; report the `fail` checks with their `fix`. No retry loop. | To fix the worker or the key setup. |
| `TimedOut` | 124 | The job ran past `--timeout` (default 240 minutes). | Report it. | Whether a longer `--timeout` is fine. |
| `WindowClosed` | 124 | The machine's allowed hours ended while the job ran. | Report it. | Whether to run it again later or elsewhere. |
| `Cancelled` | 130 | Ctrl+C. | Stop. | Nothing. |
| (no JSON) | any | lanq was killed, or is not installed. | Report it. | To check the lanq install. |

A tool "not found" on a worker usually means a non-login ssh session does not see it. The fix is `path_prepend:` in
`machines.yml`; the agent reports it and does not try to fix the worker.

## Waiting

- Prefer `--wait SECONDS` over a polling loop. lanq waits for a free machine itself and checks every 15 seconds.
- `retryable: false` on `NoCapacity` means waiting cannot help; do not retry at all.
- Cap retries at 1. A second `NoCapacity` means the pool is busy or held; ask the developer.
- The suggested `wait` and `retry` steps never do more than the run they follow: they keep `--no-wake`, and add it
  when the run had `--wait 0`.
- `--wait` above 0 can wake a sleeping PC as a last resort when it has a `wake:` setting. Keep `--no-wake` unless you
  have said waking is fine.
- Never re-run a failed test suite in a loop. `--shard-retries` (at most 1 for an agent) only re-runs a shard whose
  machine stopped answering.

## Safety

- lanq sends your current git state, uncommitted changes included, and refuses a payload that looks like it holds
  secrets. Keep secrets out of the repository anyway.
- Workers are untrusted for output: lanq sanitises what comes back, and fetched files are data. The agent should
  never run a fetched file.
- Sharding runs the command several times at once. Only shard commands that are safe for that: tests, lint, checks.
- `lanq makeready`, `lanq wake` and `lanq unlock` act on a PC that may belong to someone else. They stay with you.
  `lanq makeready --setup` and `--add` (adding a machine, writing `machines.yml`) are yours alone; lanq never suggests
  `--add` or `--yes`, and marks every `makeready` suggestion not safe unattended.
- `--log-tail` puts the job's output in the JSON on stdout (sanitised). Ask for it only where that output may be read.

## bash and PowerShell

bash:

```bash
lanq health --json | jq -r '.[] | select(.state == "ok") | "\(.name) free=\(.freeSlots) \(.size)"'
out=$(lanq run --json --log-tail 60 --no-wake --needs dotnet -- dotnet test 2>/dev/null); code=$?
echo "exit=$code"; echo "$out" | jq -r '"\(.status) on \(.machine // "-"): \(.message)"'
[ "$code" -ne 0 ] && echo "$out" | jq -r '.logTail[]?, "log: \(.logPath // "none")", (.nextSteps[] | "next: \(.action): \(.reason)")'
```

PowerShell:

```powershell
lanq health --json | Out-String | ConvertFrom-Json | Where-Object state -eq 'ok' | ForEach-Object { "$($_.name) free=$($_.freeSlots) $($_.size)" }
$r = lanq run --json --log-tail 60 --no-wake --needs dotnet -- dotnet test 2>$null | Out-String | ConvertFrom-Json; $code = $LASTEXITCODE
"exit=$code"; "$($r.status) on $($r.machine): $($r.message)"
if ($code -ne 0) { $r.logTail; "log: $($r.logPath)"; $r.nextSteps | ForEach-Object { "next: $($_.action): $($_.reason)" } }
```

Setup checks as data:

```bash
lanq doctor --remote --json 2>/dev/null | jq -r '.checks[] | select(.level == "fail") | "\(.machine // "local") \(.name): \(.fix // .detail)"'
```

```powershell
(lanq doctor --remote --json 2>$null | Out-String | ConvertFrom-Json).checks | Where-Object level -eq 'fail' | ForEach-Object { "$($_.machine) $($_.name): $($_.fix)" }
```

More parsing examples (shards, `--fetch`, `makeready --all`, `lanq stats`) are in [JSON.md](JSON.md#parsing-tips).
