<!-- Paste into your project's CLAUDE.md. Keep it short: Claude treats it as guidance, so also use
     permission rules or a hook if a rule must be enforced. -->

## Remote builds and tests (lanq)

- Run builds and tests through `lanq`, not by ssh-ing anywhere yourself:
  `lanq run --needs <capability> -- <command>`. The command must be one the developer has allowlisted.
- Capture output and read only a summary:
  `lanq run --needs dotnet -- dotnet test > "${TMPDIR:-/tmp}/lanq-last.log" 2>&1; echo "exit=$?"; tail -n 40 "${TMPDIR:-/tmp}/lanq-last.log"`
  Report the exit code and the failing tests from the tail. Open more of the log only if the tail is not enough.
  Never paste a whole log into the conversation.
- Exit codes: the job's own code; `2` refused by policy or bad arguments; `70` infrastructure error (ssh, git);
  `75` no eligible machine (retry with `--wait 120`, or tell the developer).
- Never edit the lanq machines file, the allowlists or any ssh/key configuration. The developer owns them. If a
  command is refused, say so and stop; do not look for a way around it.
- Never pass permission-skipping flags (for example `--dangerously-skip-permissions`) to any command, including
  commands run through lanq. lanq rejects them anyway.
- Never put secrets, tokens or key material in files or command lines that lanq will ship; it refuses them.
