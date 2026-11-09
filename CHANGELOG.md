# Changelog

## Unreleased (0.4.0)

Workers and windows: making old and shared PCs safe to lend to the queue.

- **Memory in health.** The probe reports available memory (Linux `MemAvailable` and free swap, plus PSI memory
  pressure `some avg10` where `/proc/pressure/memory` exists; Windows free physical memory) and `lanq health` shows
  `mem free X.X GB`. A machine with less than `policy.min_free_mem_gb` (default 1.0, per-machine `min_free_mem_gb`)
  is held ("low memory (x.x GB free)"), and on Linux one whose pressure is above `policy.max_mem_pressure` (default
  20) is held ("memory pressure"). A machine that does not report memory is not held for it.
- **`lanq run --mem <GB>`** sends a job only to a machine reporting at least that much available memory (never to one
  that cannot report it).
- **Per-machine `env:`** sets variables for the job process only, for example heap caps on small machines
  (`DOTNET_GCHeapHardLimitPercent`, `NODE_OPTIONS=--max-old-space-size=2048`, `MSBUILDNODECOUNT`,
  `CARGO_BUILD_JOBS`). Names must match `^[A-Za-z_][A-Za-z0-9_]*$`, values may not contain control characters, and
  loader or shell hijack names (`LD_PRELOAD`, `LD_LIBRARY_PATH`, `LD_AUDIT`, `DYLD_*`, `BASH_ENV`, `ENV`,
  `PROMPT_COMMAND`, `PSModulePath`, `COMSPEC`, `PATHEXT`) are refused. Values are passed as quoted data in both
  shells.
- **Per-machine `path_prepend:`** puts absolute directories in front of `PATH` for the job, so tools installed by nvm,
  pyenv, rustup or sdkman are found in a non-login SSH session. `PATH` itself cannot be set through `env:`.
- **`priority: low|normal`** (default `low`): Linux jobs run under `nice -n 10` and `ionice -c3` when available;
  Windows jobs run at BelowNormal priority, inherited by their child processes. **This changes the default for
  existing configs**; set `priority: normal` on a dedicated build box.
- **PAUSE file.** If `<work_root>/PAUSE` exists on a worker, the machine is held ("paused on the machine"). Anyone at
  that PC can create or delete it. A job that is already running is not affected.
- **Availability windows v2.** `allowed_hours` still accepts one string, and now also a list of entries
  `[Days ]HH:mm-HH:mm` with days `Mon`..`Sun`, ranges (`Mon-Fri`) and lists (`Sat,Sun`). A window that crosses
  midnight belongs to its start day. The start minute is inside a window and the end minute is not (0.3 also counted
  the end minute). Errors name the bad entry.
- **Windows use the worker's clock.** The probe reports the worker's local time; output from an older probe falls back
  to the clock of the machine running `lanq`, and `lanq health` then says `time: local`.
- **Window close.** On a machine with windows a job may run until the window ends plus `policy.window_grace_minutes`
  (default 15) or until `--timeout`, whichever is sooner. If the window limit stops it, the job is cleaned up like a
  timeout, exits `124`, and the status line is `[lanq] WindowClosed`. A machine whose window ends in less than
  `policy.min_window_minutes` (default 10) takes no new job ("window closing").

## 0.3.1 (2026-11-09)

- **Security: PowerShell quoting.** Windows arguments are single-quoted for PowerShell, which also treats the
  typographic quotes U+201A and U+201B as single quotes. They were not escaped, so an argument containing one could
  close the string and run code on a Windows worker. All five single-quote characters are now doubled, checked by a
  round trip through a real PowerShell in the tests.
- **Security: cmd.exe metacharacters on Windows.** PowerShell 5.1 passes embedded double quotes to native programs
  unescaped, and `.cmd` shims such as `npm.cmd` re-parse the command line through `cmd.exe`, so `npm test "&calc"`
  could start a second program. A job whose arguments contain `"` `&` `|` `<` `>` `^` `%` now skips Windows workers
  and runs on a Linux worker; it is refused (exit 2, before any network work) only when no Linux worker has the
  needed capabilities. Linux workers are unchanged.
- **Security: untrusted output.** Control characters (C0 except tab, DEL, C1), bidi controls and zero-width
  characters from a worker are now shown as `?` on the console and in the job log, so a job cannot move the cursor,
  clear the screen, retitle the window or disguise text. The same applies to the error line `lanq health` prints.
- **ssh hardening.** ssh and scp always pass `ForwardAgent=no`, `ClearAllForwardings=yes`, `PermitLocalCommand=no`,
  `ForwardX11=no`, `ForwardX11Trusted=no`, `Tunnel=no` and `RequestTTY=no`, whatever `~/.ssh/config` says.
- **Timeouts.** A job that runs past its limit now ends with exit code `124` and "timed out after N min" instead of
  being reported as an SSH failure (`70`). New `lanq run --timeout <minutes>` (default 240). A job's own exit 124 is
  told apart by the `[lanq] TimedOut` status line.
- **Bounded memory.** Worker output is read in small chunks (LF, CR and CRLF each end a line, as before) and a single line is capped at 16 KB (cut with a
  marker), so even a newline-free flood costs a fixed amount of memory. A long job keeps only the last 64 KB for its
  result (the full output still streams to the console and the log); the health probe and slot claim keep only the
  first 64 KB. No output line is delivered after a command has returned, so a timed-out job cannot crash `lanq`.
- **Faster health checks.** `lanq health` and `lanq run` probe all machines at once instead of one after another;
  output order is unchanged.
- `allowed_hours` with the same start and end (for example `"22:00-22:00"`) is now a config error instead of a
  window that matches a single minute.

## 0.3.0

- **Archive transfer mode.** A machine can set `transfer: archive` in `machines.yml` (default stays `git`, the
  recommended mode). The same snapshot (HEAD plus uncommitted and untracked files, honouring `.gitignore`, never
  `.lanq/`) is sent as a deterministic `tar.gz` and extracted by the worker's `tar` into a fresh per-job folder, so a
  worker no longer needs git. Guarantees: `lanq` never builds an archive with an absolute, `..`, backslash or
  Windows-unstorable path, and skips (and reports) symlinks and submodules; the worker accepts only GNU tar or bsdtar (the
  Windows built-in one on Windows) (busybox tar workers are refused, gzip is required on Linux), lists the archive first and
  refuses it, writing nothing, if any entry is absolute, has `..` or a backslash, is not a plain file or directory,
  or is listed as a link (hard links and symlinks). Windows workers call `System32\tar.exe` by full path, the health probe checks once that it accepts the restricting
  flags (`--no-acls --no-xattrs --no-fflags --no-same-owner --no-same-permissions`), the extract runs exactly once
  with them (no fallback, stderr kept in the job log), and cleanup
  verifies the job folder is really gone. The secret scan, allowlists, directory checks and process-group kill are
  unchanged. Opt-in tests (`LANQ_TAR_TESTS=1`) run the generated scripts against hostile archives.
- The archive is built only when an archive machine actually gets the job, so git-only fleets never build one.
- `lanq health` checks `git --version` on git machines and a supported `tar` on archive machines, and holds a machine
  that lacks its tool, with the reason.
- `transfer:` now accepts only the words `git` or `archive`.
- NuGet-specific package readme and published install instructions.
- Old PCs pitch: a worker needs only a LAN connection, SSH, tar (or git) and your toolchain.

## 0.2.0

- Documentation: keeping SSH keys safe, how it works, using lanq with coding agents.
- Safer defaults from pre-publish review: secret scan hardening (bounded patterns, passphrase and URL-credential
  rules), per-user state location outside the repository, job process-group kill on cleanup, stricter health
  parsing.
- Release to NuGet via Trusted Publishing, and a worker setup guide.

## 0.1.0

- First release: headless, capability-routed job runner for LAN machines over key-only SSH.
