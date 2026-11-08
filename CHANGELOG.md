# Changelog

## 0.3.0

- **Archive transfer mode.** A machine can set `transfer: archive` in `machines.yml` (default stays `git`, the
  recommended mode). The same snapshot (HEAD plus uncommitted and untracked files, honouring `.gitignore`, never
  `.lanq/`) is sent as a deterministic `tar.gz` and extracted by the worker's built-in `tar` into a fresh per-job
  folder, so a worker no longer needs git. The archive lists are checked on both ends (no absolute or `..` paths, no
  symlinks or hard links); the secret scan, allowlists, directory checks, process-group kill and cleanup are unchanged.
- `lanq health` checks `git --version` on git machines and `tar` on archive machines, and holds a machine that lacks
  its tool, with the reason.
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
