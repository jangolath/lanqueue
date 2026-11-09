# Changelog

## Unreleased (0.4.0)

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
