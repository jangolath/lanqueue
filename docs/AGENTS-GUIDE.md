# Using lanq from an AI coding agent

Coding agents are good at deciding what to build and test. They waste context working out how to reach a remote
box: composing ssh commands, retrying, pasting long logs back. With `lanq` the agent runs one command, reads one JSON
object and a short log tail, and moves on. The remote mechanics, the allowlist and the cleanup live in lanq.

This page is for the developer who sets it up. The short rules an agent reads are in [AGENTS.md](../AGENTS.md); every
JSON field is in [JSON.md](JSON.md).

## Who owns what

| | The developer | The agent |
| --- | --- | --- |
| `machines.yml`, `policy.allowed_commands`, `policy.allowed_directories` | writes and owns | never edits |
| SSH keys, ssh-agent, `known_hosts` | sets up | never touches |
| Workers (OpenSSH, tools, `PAUSE` files) | sets up, or asks each PC's owner | never touches |
| `lanq makeready`, `lanq unlock`, `lanq wake`, `lanq health --wake`, `lanq init` | runs | only when asked |
| `lanq health --json`, `lanq run --json`, `lanq doctor` | runs | runs |

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
- Run: lanq run --json --no-wake --needs <capability> -- <allowlisted command>
  Send stderr to a uniquely named log file and read only its last 40 lines. Never paste a whole log.
- stdout is one JSON object. Act on "status": Completed = passed; Failed = summarise the failures;
  Rejected = report and stop; NoCapacity = retry once with --wait 120 --no-wake, then ask;
  InfraError = run lanq doctor --remote and report; TimedOut / WindowClosed = report; Cancelled = stop.
- Never edit lanq's machines.yml, allowlists or SSH keys. Never pass permission-skipping flags.
- Do not run lanq wake, lanq health --wake, lanq makeready, lanq unlock or lanq init unless asked.
- Never shard a command that is not idempotent. Retry at most once.
```

## The standard loop

1. `lanq health --json`. Pick capabilities and, for a heavy job, a `size` that suits it (`--needs bigmem` if you tag
   machines that way, or `--mem GB`).
2. `lanq run --json --no-wake --needs <caps> [--dir D] [--fetch PATTERN] -- <command>`, stderr to a unique log file.
   A long suite that splits: `--shards auto` (see [SHARDING.md](SHARDING.md)).
3. Read the exit code, then `status`. Read the log tail only to explain a failure.
4. Report in a line or two: status, machine, and the failing tests or the message.

## Failure playbook

| `status` | Exit | Likely cause | What the agent does | What to ask the developer |
| --- | --- | --- | --- | --- |
| `Failed` | job's code | The tests or build failed. | Summarise the failures from the tail; fix the code if that is the task. | Nothing, unless the failure looks environmental (a missing tool, a full disk). |
| `Rejected` | 2 | Not on the allowlist, a bad flag, a permission-skipping flag, a secret in the payload, a config error. | Report the message. Stop. | Whether to allowlist the command or fix the config. |
| `NoCapacity` | 75 | Every machine busy, held (hours, pause, memory, load) or lacking a `--needs` tag. | Retry once with `--wait 120 --no-wake`. | Whether to wait, wake a machine, or run locally. |
| `InfraError` | 70 | ssh or git trouble, a machine that stopped answering, a broken snapshot. | Run `lanq doctor --remote`; report its FAIL lines. No retry loop. | To fix the worker or the key setup. |
| `TimedOut` | 124 | The job ran past `--timeout` (default 240 minutes). | Report it. | Whether a longer `--timeout` is fine. |
| `WindowClosed` | 124 | The machine's allowed hours ended while the job ran. | Report it. | Whether to run it again later or elsewhere. |
| `Cancelled` | 130 | Ctrl+C. | Stop. | Nothing. |
| (no JSON) | any | lanq was killed, or is not installed. | Report it. | To check the lanq install. |

A tool "not found" on a worker usually means a non-login ssh session does not see it. The fix is `path_prepend:` in
`machines.yml`; the agent reports it and does not try to fix the worker.

## Waiting

- Prefer `--wait SECONDS` over a polling loop. lanq waits for a free machine itself and checks every 15 seconds.
- Cap retries at 1. A second `NoCapacity` means the pool is busy or held; ask the developer.
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

## bash and PowerShell

bash:

```bash
lanq health --json | jq -r '.[] | select(.state == "ok") | "\(.name) free=\(.freeSlots) \(.size)"'
log="${TMPDIR:-/tmp}/lanq-$(date +%s)-$$.log"
out=$(lanq run --json --no-wake --needs dotnet -- dotnet test 2> "$log"); code=$?
echo "exit=$code"; echo "$out" | jq -r '"\(.status) on \(.machine // "-"): \(.message)"'
[ "$code" -ne 0 ] && tail -n 40 "$log"
```

PowerShell:

```powershell
lanq health --json | Out-String | ConvertFrom-Json | Where-Object state -eq 'ok' | ForEach-Object { "$($_.name) free=$($_.freeSlots) $($_.size)" }
$log = Join-Path $env:TEMP ("lanq-{0}-{1}.log" -f (Get-Date -Format yyyyMMdd-HHmmss), $PID)
$r = lanq run --json --no-wake --needs dotnet -- dotnet test 2> $log | Out-String | ConvertFrom-Json; $code = $LASTEXITCODE
"exit=$code"; "$($r.status) on $($r.machine): $($r.message)"
if ($code -ne 0) { Get-Content $log -Tail 40 }
```

More parsing examples (shards, `--fetch`, `makeready --all`) are in [JSON.md](JSON.md#parsing-tips).
