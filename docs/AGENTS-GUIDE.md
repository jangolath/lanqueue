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
| `machines.yml`, `policy.allowed_commands`, `policy.allowed_directories`, `policy.allowed_env` | writes and owns | never edits, not even to get an `--env` through |
| `lanq.yml` (the repository's plan file) | writes and reviews with the team | never edits to get around a refusal |
| SSH keys, ssh-agent, `known_hosts` | sets up | never touches |
| Workers (OpenSSH, tools, `PAUSE` files) | sets up, or asks each PC's owner | never touches |
| `lanq makeready --setup`, `lanq makeready --add` (and its `--yes`), `lanq init` | runs | never |
| `lanq makeready`, `lanq unlock`, `lanq wake`, `lanq health --wake` | runs | only when asked |
| lanq-makeready-fix (`lanq-makeready-fix.ps1` / `.sh`, which `lanq makeready` may point to) | passes on to the PC's owner, who runs it at that PC (local and interactive only) | never |
| `lanq health --json` (also `--needs`, `--on`, `--size`, `--min-size`), `lanq run --json` (also `--dry-run`, `--on`, `--size`, `--min-size`, `--exclusive`, `--baseline`, `--baseline-ref`), `lanq run --plan --json --no-wake` (also `--only`, `--dry-run`), `lanq doctor --json` (also `--remote`), `lanq stats --json` | runs | runs |
| `lanq run --env NAME=VALUE` | lists the names jobs may set in `policy.allowed_env` | runs, only with names already listed; never secrets |
| `jobs.jsonl`, `jobs.1.jsonl` and `logs/` in lanq's state folder | owns (lanq writes them) | never touches (reads them through `lanq stats --json` and `logPath`) |
| `nextSteps` with `safeToRunUnattended: true` | | runs, at most once |

The hard stops are in lanq, not in the agent's instructions: it refuses commands outside your allowlist,
permission-skipping flags and secrets in the payload. Text in an instruction file is guidance the model follows. To
also keep the agent away from the config file, use your agent's permission settings.

## Setup

1. Set up the pool: [TEAM-QUICKSTART.md](TEAM-QUICKSTART.md) for several machines, or the README's
   [Quick start](../README.md#quick-start) for one.
2. Allowlist only what the agent should run, for example `dotnet test`, `npm test`, `npx jest`. If jobs need their own
   variables (a configuration, a test filter), list those names in `policy.allowed_env`; the agent cannot add any.
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
- Never run lanq-makeready-fix: it is the PC owner's, local and interactive only. Report it to the developer.
- Never shard a command that is not idempotent. Retry at most once.
- Long suites: --shards auto --target-minutes 10 once lanq stats --json shows a "timing" for the command; preview
  with --dry-run.
- Route with --size light,medium / --min-size heavy or --on <machine>. A repository with lanq.yml: lanq run --plan
  --json --no-wake [--only a,b] (preview with --dry-run); report each job in "jobs" whose status is not Completed,
  by name. Never edit lanq.yml or machines.yml to get around a refusal.
- --env NAME=VALUE only for names already in policy.allowed_env; never edit allowed_env or machines.yml to get one
  through, and never put a secret in a value. excluded reasons env_conflict and windows_command_too_long: report them
  to the developer. An ask_developer step for an env refusal means stop and report; never drop variables or edit
  machines.yml or lanq.yml to get through.
- --exclusive gives a job a whole machine; it waits for an idle one (no reservation).
- --baseline reruns a Failed job at the base commit. baseline.verdict "preexisting": report the failure as
  pre-existing and do not fix it unless that is your task. "new" with sameMachine false ran on another machine:
  report it with that caveat (it may be machine-specific), not as certainly new. Status and exit code never change.
- Never edit or delete lanq's jobs.jsonl or logs/ folder.
```

## The standard loop

1. `lanq health --json`. Pick capabilities and, for a heavy job, a `size` that suits it (`--min-size heavy`,
   `--needs bigmem` if you tag machines that way, or `--mem GB`; `--on <machine>` pins it). `lanq health --needs <caps> [--mem GB] --json` says per machine whether it
   could take such a job now (`eligible`, `why`, `jobSlots`); `lanq run ... --dry-run --json` says which machine the
   run would take, and why the others are out, without sending anything.
2. `lanq run --json --log-tail 60 --no-wake --needs <caps> [--dir D] [--fetch PATTERN] -- <command>`, with stderr
   dropped (the log is in `logTail` and at `logPath`). A long suite that splits: `--shards auto` (see
   [SHARDING.md](SHARDING.md)); once `lanq stats --json` shows a `timing` for the command,
   `--shards auto --target-minutes 10`, previewed with `--dry-run`.
   A repository with a `lanq.yml`: `lanq run --plan --json --log-tail 60 --no-wake` runs all its jobs
   ([PLANS.md](PLANS.md)), `--only a,b` some of them, `--dry-run` previews.
3. Read the exit code, then `status`, then `nextSteps`. Read `logTail` only to explain a failure.
4. Report in a line or two: status, machine, and the failing tests or the message. For a plan, each job that is not
   `Completed`, by name, with its `message`. With `--baseline`, add the verdict: a `preexisting` failure was there
   before the change, and the agent reports it rather than fixing it, unless that is the task.

## Job options for agents

| Option | The agent may | Notes |
| --- | --- | --- |
| `--env NAME=VALUE` | use it with names already in `policy.allowed_env` | A name not listed is `Rejected` (2) with an `ask_developer` step, and so are values too long for every Windows machine left and an `env_conflict` on every machine: the agent stops and reports. A deny-table name on its own command line is `fix_command` ("drop it or ask the developer"); from `lanq.yml` it is `ask_developer`. The agent never edits `allowed_env`, `machines.yml` or `lanq.yml` to get one through. Values are settings, not secrets: other users of a worker can see them, and `nextSteps` commands repeat them. |
| `--exclusive` | use it when the job needs a whole machine | It waits for an idle machine (no reservation), so it may end `NoCapacity` (75, retryable) on a busy pool. |
| `--baseline`, `--baseline-ref REF` | use them | After a `Failed` run, a rerun at the base commit adds `baseline.verdict`: `new`, `preexisting`, `inconclusive` or `skipped`. Status and exit code never change. It doubles the time of a red run. A `new` verdict with `sameMachine: false` ran on another machine: the agent reports it with that caveat, not as certainly new. |

## Failure playbook

| `status` | Exit | Likely cause | What the agent does | What to ask the developer |
| --- | --- | --- | --- | --- |
| `Failed` | job's code | The tests or build failed. | Summarise the failures from the tail; fix the code if that is the task. With `--baseline` and verdict `preexisting`: report it as pre-existing, do not fix it unless that is the task. | Nothing, unless the failure looks environmental (a missing tool, a full disk) or is pre-existing. |
| `Rejected` | 2 | Not on the allowlist, a bad flag, a permission-skipping flag, a secret in the payload (also in a file git shows as binary, since 0.9.2: `uncommitted file <path> (binary per git)`) or an `--env` value, an `--env` name not in `policy.allowed_env` or on the deny table, a config error. `scan-incomplete`: lanq could not read such a file from git. | Report the message. Stop. Never hide a file from the scan (attributes, renames, compression, moves). | Whether to allowlist the command or the variable name, or fix the config; for `scan-incomplete`, to check the repository with `git fsck`. |
| `NoCapacity`, `retryable: true` | 75 | Every machine busy, held (hours, pause, memory, load), asleep or unreachable. | Run the `wait` step from `nextSteps` once (the same run with `--wait 120`). | Whether to wait, wake a machine, or run locally. |
| `NoCapacity`, `retryable: false` | 75 | No machine can ever take the job as configured: none has a `--needs` tag, none is of the `--size`/`--min-size` asked for, `--mem` is above every machine's RAM, git or tar is missing, or the job's `--env` clashes with each machine's own `env:` (`env_conflict`) or is too long for it (`windows_command_too_long`). lanq ended at once. | Stop. Report `excluded`. Do not drop `--on`/`--size`/`--env` or edit `lanq.yml` or `machines.yml` to get it through. | Whether to add the capability or a machine, change the job's routing or variables, change a machine's `env:`, or run locally. |
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
  secrets. Keep secrets out of the repository anyway. Since 0.9.2 the scan also reads files git shows as binary (a
  `.gitattributes` `-diff` or `binary` entry hides a file from the diff, not from lanq), up to its caps. The agent
  never adds an attribute, renames, compresses, moves or re-encodes a file, or moves a secret into a `-diff` file, to
  get past the scan. `scanSkipped` lists files over the caps that were not read in full: the agent reports them and
  does not try to get them read.
- `lanq makeready --json` (and `--all`, `--add`) prints the worker's report sanitised; `suggestedMachine.path_prepend`
  and `env` appear only when lanq would use them, and `lanqWithheld` (lanq's own key) says what was left out.
  `suggestedMachine` is advice for the developer: the agent never writes it into `machines.yml`.
- In lanq's own repository the agent may run `lanq run --plan --json --no-wake` (its `lanq.yml`), never edits
  `lanq.yml` or `scripts/lanq/*` to turn a red run green, and runs `scripts/smoke-workers.ps1` only when the developer
  asks (it needs their `machines.smoke.yml`, in the same per-user folder as `machines.yml`: `%APPDATA%\lanq\machines.smoke.yml` on Windows, `~/.config/lanq/machines.smoke.yml` (or `$XDG_CONFIG_HOME/lanq/machines.smoke.yml`) elsewhere; the agent never edits that file or
  `machines.yml`). The repository's `machines.example.yml` is only a template; lanq never reads it.
- Workers are untrusted for output: lanq sanitises what comes back, and fetched files are data. The agent should
  never run a fetched file.
- Sharding runs the command several times at once. Only shard commands that are safe for that: tests, lint, checks.
- `lanq makeready`, `lanq wake` and `lanq unlock` act on a PC that may belong to someone else. They stay with you.
  The one exception is lanq's release smoke (`scripts/smoke-workers.ps1`), which runs `lanq unlock` on its smoke
  machines when the developer has asked for the smoke.
  `lanq makeready --setup` and `--add` (adding a machine, writing `machines.yml`) are yours alone; lanq never suggests
  `--add` or `--yes`, and marks every `makeready` suggestion not safe unattended. When `lanq makeready` says a check may
  be fixable at that PC, it means lanq-makeready-fix, which only the PC's owner runs, at the PC (local and interactive; it
  refuses to run over SSH). lanq never sends or runs it, and neither does an agent.
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
