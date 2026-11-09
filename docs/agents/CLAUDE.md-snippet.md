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
- To see which machines are free: `lanq health --json` (an array of `name`, `state` ok|hold|down, `reasons`, `load`,
  `diskFreeGb`, `memFreeGb`, `slotsUsed`, `maxJobs`, `localTime`). If a tool is "not found" on a worker, `lanq doctor
  --remote` shows which one; tell the developer (the fix is `path_prepend:` in their config, not yours to make).
- Exit codes: the job's own code; `2` refused by policy or bad arguments; `70` infrastructure error (ssh, git);
  `75` no eligible machine (retry with `--wait 120`, or tell the developer).
- Never edit the lanq machines file, the allowlists or any ssh/key configuration. The developer owns them. If a
  command is refused, say so and stop; do not look for a way around it.
- Do not run `lanq unlock` or `lanq makeready` unless the developer asks: they act on someone's PC.
- Never pass permission-skipping flags (for example `--dangerously-skip-permissions`) to any command, including
  commands run through lanq. lanq rejects them anyway.
- Never put secrets, tokens or key material in files or command lines that lanq will ship; it refuses them.
