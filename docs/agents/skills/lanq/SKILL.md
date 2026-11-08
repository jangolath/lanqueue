---
name: lanq
description: Run builds, tests and other allowlisted commands on the developer's spare LAN machines through the lanq CLI instead of locally or by hand-written ssh. Use when asked to build or test on a remote box, when a build or test run would be heavy, or when the local machine is busy.
---

# Run work on LAN machines with lanq

`lanq` ships the current git state (HEAD plus uncommitted changes) to the least-loaded capable machine, runs one
allowlisted command in a throw-away worktree, streams the log, cleans up and returns the job's exit code.

## When to use

- A build or test run that is heavy or slow, or the local machine is busy.
- The developer asks for a build or test "on the build box" or "remotely".

Do not use it for commands outside the allowlist, for interactive programs, or when no `lanq` config exists.

## Commands

Check who is available:

```bash
lanq health
```

Run a job (run from inside the git repository) and keep only a short result:

```bash
lanq run --needs dotnet -- dotnet test -c Release > "${TMPDIR:-/tmp}/lanq-last.log" 2>&1; echo "exit=$?"; tail -n 40 "${TMPDIR:-/tmp}/lanq-last.log"
```

- `--needs a,b` requires every listed capability on the machine (tags come from the developer's config).
- `--dir <relative dir>` runs in a subdirectory, for example `--dir tests`.
- `--wait 120` retries for up to 120 seconds while every machine is busy.
- lanq keeps full per-job logs in its own state folder (`$LANQ_HOME`, else a per-user folder), never in the repository.

## Reading the result

- Exit `0`: the job passed. Say so in one line.
- Same exit code as the command (for example `1`): the job ran and failed. Summarise the failures from the tail; open
  the log file further only if the tail does not show them.
- `2`: refused by policy (command, directory, secret scan, bad flag). Report the message and stop.
- `70`: infrastructure error (ssh, git). Report it; do not retry in a loop.
- `75`: no eligible machine. Retry once with `--wait 120`, then tell the developer.

## What not to do

- Do not edit the lanq machines file, the allowlists, `known_hosts` or any ssh or key setting. The developer owns them.
- Do not ssh to the machines yourself, and do not try to work around a refusal.
- Do not pass permission-skipping flags such as `--dangerously-skip-permissions`; lanq rejects them.
- Do not paste whole logs into the conversation.
