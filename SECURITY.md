# Security policy

## Reporting a vulnerability

Please report privately using GitHub's "Report a vulnerability" (private security advisory) on this repository.
Do not open a public issue for security problems. Expect an acknowledgement within a few days; this is a
volunteer-maintained project.

## Scope and threat model

`lanq` is for a single developer's own, trusted LAN machines. It reduces risk with key-only SSH, strict host key
checking, a command and directory allowlist, a best-effort secret scan and a ban on permission-skipping agent flags.

On Windows workers every argument is passed to PowerShell as a single-quoted literal (all five PowerShell
single-quote characters are escaped), and arguments containing `"` `&` `|` `<` `>` `^` `%` never go to a Windows worker, because
PowerShell 5.1 native-argument passing and `cmd.exe` (which runs `.cmd` shims such as `npm.cmd`) would re-parse
them. Output from a worker is treated as untrusted: control and invisible formatting characters are shown as `?`.

Per-machine `env:` values reach the job as data (one single-quoted word for `env` on Linux, a single-quoted
PowerShell string on Windows) and never as shell code; names are restricted to `[A-Za-z_][A-Za-z0-9_]*`, values may
not contain control characters, and names that hijack the program loader or a shell (`LD_PRELOAD`,
`LD_LIBRARY_PATH`, `LD_AUDIT`, `DYLD_*`, `BASH_ENV`, `ENV`, `PROMPT_COMMAND`, `PSModulePath`, `COMSPEC`, `PATHEXT`)
are refused. `path_prepend` entries must be absolute. Both are written by you in your own config: they are a
convenience for your machines, not a boundary against someone who can edit that file.

It does not:
- sandbox the allowlisted program (it runs with the remote user's rights; use a low-privilege account);
- guarantee that no secret leaves your machine (the scan is pattern-based);
- protect against a compromised remote host or a compromised local machine.

Supported version: the latest release.
