# Security policy

## Reporting a vulnerability

Please report privately using GitHub's "Report a vulnerability" (private security advisory) on this repository.
Do not open a public issue for security problems. Expect an acknowledgement within a few days; this is a
volunteer-maintained project.

## Scope and threat model

`lanq` is for a single developer's own, trusted LAN machines. It reduces risk with key-only SSH, strict host key
checking, a command and directory allowlist, secret refusal and a ban on permission-skipping agent flags.

It does not:
- sandbox the allowlisted program (it runs with the remote user's rights; use a low-privilege account);
- guarantee that no secret leaves your machine (the scan is pattern-based);
- protect against a compromised remote host or a compromised local machine.

Supported version: the latest release.
