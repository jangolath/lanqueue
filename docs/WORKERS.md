# What each worker needs

A *worker* is any machine on your network that `lanq` sends jobs to. Nothing from lanqueue is installed on it:
your own PC runs `lanq`, ships the code over SSH as a git bundle, and the worker only needs the pieces below.

## Checklist

| Need | Required? | Why | How to check on the worker |
|---|---|---|---|
| SSH server accepting your public key | **Required** | every lanq call is key-only SSH | from your PC: `ssh -o BatchMode=yes <user>@<worker> echo ok` |
| Git | **Required** | the bundle is unpacked into a throwaway worktree | `git --version` |
| Shell | **Required** | Windows: Windows PowerShell (built in). Linux: `sh` | — |
| Writable work folder (`work_root`) with free disk | **Required** | each job gets its own worktree there; `lanq health` holds a machine with low disk | `lanq health` from your PC |
| Toolchain your jobs run (e.g. .NET SDK, Node) | Per job | tag the machine with matching `capabilities` (`dotnet`, `node`, ...) | `dotnet --version`, `node --version` |
| Docker | Only for jobs that need it | e.g. integration tests with Testcontainers; tag the machine `docker` | `docker version` |
| Internet access to package registries | Only if jobs restore packages | `dotnet restore` / `npm ci` download from nuget.org / npm | — |
| Credentials for private package feeds | Only if you use private feeds | configure them **on the worker** (user-level config); never pass secrets in a job | — |

**Not needed:** GitHub access or GitHub credentials (the worker never clones from GitHub), lanqueue itself, an
agent such as Claude Code, or admin rights for the build user.

## Setting up SSH on a worker

**Windows** (elevated PowerShell; see Microsoft's
[Get started with OpenSSH for Windows](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)):

```powershell
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
Start-Service sshd
Set-Service -Name sshd -StartupType 'Automatic'
```

Put your public key in the build user's `C:\Users\<user>\.ssh\authorized_keys` (for an administrator account,
Windows reads `C:\ProgramData\ssh\administrators_authorized_keys` instead; a dedicated non-admin build user is
simpler and safer). See "Keeping your keys safe" in the README for `restrict` and `from=` options.

**Linux** (Debian/Ubuntu shown):

```sh
sudo apt install openssh-server git
sudo systemctl enable --now ssh
```

Add your public key to `~/.ssh/authorized_keys` of the build user (`chmod 600`).

## Then, on your PC

1. Add the worker to your `machines.yml` (see `machines.example.yml`): name, `os`, `ssh` target, `work_root`,
   `capabilities`, `max_jobs`, optional `allowed_hours`.
2. Make sure the worker's host key is in your `known_hosts` (connect once with `ssh` and verify the fingerprint):
   lanq uses `StrictHostKeyChecking=yes`.
3. Run `lanq health`: the worker should show as ready, or as "hold short" with the reason.

## For AI agents reading this

- You may **use** workers through `lanq run --needs <capabilities> -- <allowlisted command>`; see
  `docs/agents/`.
- You must **not** set up workers, edit `machines.yml`, change allowlists, install software on workers, or handle
  SSH keys: that is the developer's job. If a job fails because a worker lacks a tool, report which capability is
  missing instead of trying to fix the worker.
