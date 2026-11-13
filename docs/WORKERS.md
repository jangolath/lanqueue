# What each worker needs

A *worker* is any machine on your network that `lanq` sends jobs to. Nothing from lanqueue is installed on it:
your own PC runs `lanq`, ships the code over SSH (as a git bundle by default, or as a `tar.gz` with
`transfer: archive`), and the worker only needs the pieces below. **An old PC needs only SSH, tar and your toolchain.**

## Check a machine first: lanq-makeready

Not sure an old PC is up to it? [lanq-makeready](https://github.com/jangolath/lanq-makeready) is a read-only check
(Windows, Linux, macOS; no admin, no network, nothing installed) that reports what is missing or too old for this
checklist, what would break an overnight job (sleep, pending reboot, update restarts), and a ready-to-paste
`machines.yml` entry. Two ways to run it:

- **From your PC, once SSH works** (the onboarding case): `lanq makeready --ssh <user>@<worker> --os windows` (or
  `--os linux`). `lanq` has the script built in, sends it on ssh stdin and reads back a redacted JSON report; nothing
  is written to the worker's disk. Later, for a machine already in `machines.yml`, `lanq makeready <name>` also shows
  which capabilities are detected but not configured, or configured but not detected. It respects that machine's
  `PAUSE` file and `allowed_hours` unless you add `--force`.
- **On the PC itself**, before SSH is set up: download the script from its release page and run it there.

Either way, lanq never changes a worker: every fix it suggests is for the PC's owner to run on that PC. Once the
machines are in `machines.yml`, `lanq makeready --all` checks them all (four at a time) and prints one summary table.

Setting up several workers for a team at once? [TEAM-QUICKSTART.md](TEAM-QUICKSTART.md) walks through it step by step,
including Windows 10 build users, low-RAM micro PCs and overnight family PCs.

## Checklist

| Need | Required? | Why | How to check on the worker |
|---|---|---|---|
| SSH server accepting your public key | **Required** | every lanq call is key-only SSH | from your PC: `ssh -o BatchMode=yes <user>@<worker> echo ok` |
| Git | **Recommended; required for the default `transfer: git` mode.** Optional with `transfer: archive` | git mode unpacks the bundle into a throwaway worktree | `git --version` |
| tar (+ gzip on Linux) | Only with `transfer: archive`. On Linux must be **GNU tar or bsdtar**; on Windows the **built-in bsdtar** (`%SystemRoot%\System32\tar.exe`, Windows 10 1803+ / Server 2019+). **busybox tar (Alpine, many minimal images) is refused**: `lanq health` holds the machine as unsupported | archive mode extracts the `tar.gz` into a fresh job folder; lanq trusts the pre-extraction listing only from these tars | `tar --version` must say `GNU tar` or `bsdtar` (Windows: `%SystemRoot%\System32\tar.exe --version` says `bsdtar`); `command -v gzip` on Linux |
| Shell | **Required** | Windows: Windows PowerShell (built in). Linux: `sh` | — |
| `base64`, `sha256sum`, `find`, `awk`, `sort`, `readlink` (Linux) | Only for `lanq run --fetch` | the fetch script lists the files and sends them back as checksummed text. Standard on every distribution (coreutils, findutils; busybox has them too); Windows needs nothing extra | `command -v base64 sha256sum find awk sort readlink` |
| Writable work folder (`work_root`) with free disk | **Required** | each job gets its own worktree or folder there; `lanq health` holds a machine with low disk | `lanq health` from your PC |
| Toolchain your jobs run (e.g. .NET SDK, Node) | Per job | tag the machine with matching `capabilities` (`dotnet`, `node`, ...); for `dotnet` an SDK, not only a runtime | `dotnet --list-sdks`, `node --version`; see [Toolchain in a non-standard place](#toolchain-in-a-non-standard-place) |
| Docker | Only for jobs that need it | e.g. integration tests with Testcontainers; tag the machine `docker` | `docker version` |
| Internet access to package registries | Only if jobs restore packages | `dotnet restore` / `npm ci` download from nuget.org / npm | — |
| Credentials for private package feeds | Only if you use private feeds | configure them **on the worker** (user-level config); never pass secrets in a job | — |

**Not needed:** GitHub access or GitHub credentials (the worker never clones from GitHub), lanqueue itself, an
agent such as Claude Code, or admin rights for the build user.

## Setting up SSH on a worker

`lanq makeready --ssh build@<worker> --os windows --setup` (or `--os linux`) prints every step below for that PC, with
your public key filled in, and connects to nothing. [TEAM-QUICKSTART.md, section 3](TEAM-QUICKSTART.md#3-set-up-each-worker)
explains each step.

**Windows** (see Microsoft's
[Get started with OpenSSH for Windows](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)):

```powershell
# PowerShell (as admin), on the worker
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
Start-Service sshd
Set-Service -Name sshd -StartupType 'Automatic'
```

Use a dedicated non-admin build user. Create it so that Windows creates its profile **before** you add the key: a
`C:\Users\<user>` folder made by hand makes Windows put the real profile elsewhere (`C:\Users\<user>.PCNAME`), where
sshd never looks. Then put your public key in `<profile>\.ssh\authorized_keys`, readable only by the user, SYSTEM and
Administrators (SIDs, so it works on a Windows in any language):

```powershell
# PowerShell (as admin), on the worker
New-LocalUser -Name build -Password (Read-Host -AsSecureString -Prompt 'Password for build') -PasswordNeverExpires
Add-LocalGroupMember -SID S-1-5-32-545 -Member build     # the built-in Users group, by SID (any Windows language)
runas /user:build "cmd /c exit"                          # type the password: logs on once, which creates the profile
$sid = (Get-LocalUser build).SID.Value
$p = (Get-CimInstance Win32_UserProfile | Where-Object SID -eq $sid).LocalPath
Test-Path "$p\NTUSER.DAT"                                 # must print True; if not, check the password and run the runas line again
New-Item -ItemType Directory -Force "$p\.ssh" | Out-Null
Set-Content -Path "$p\.ssh\authorized_keys" -Value 'ssh-ed25519 AAAA...your key... lanq' -Encoding ascii
icacls "$p\.ssh\authorized_keys" /inheritance:r /grant "*${sid}:F" /grant '*S-1-5-18:F' /grant '*S-1-5-32-544:F'
```

The work folder needs the same care: a folder made on `C:\` inherits rights that let every signed-in user change it,
so cut inheritance and grant by SID (`New-Item -ItemType Directory -Force C:\lq`, then
`icacls C:\lq /inheritance:r /grant "*${sid}:(OI)(CI)M" /grant '*S-1-5-18:(OI)(CI)F' /grant '*S-1-5-32-544:(OI)(CI)F'`).

For an administrator account, Windows reads `C:\ProgramData\ssh\administrators_authorized_keys` instead, which must
grant only Administrators and SYSTEM (`icacls` with `/inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F'`); a
non-admin build user avoids that. Once the key login works, turn password logins off and limit the firewall rule to
private networks and your subnet: [TEAM-QUICKSTART.md, Windows worker](TEAM-QUICKSTART.md#windows-1011-worker),
steps 2 and 8. See "Keeping your keys safe" in the README for `restrict` and `from=` options.

**Linux** (Debian/Ubuntu shown):

```sh
# bash, on the worker
sudo apt update
sudo apt install openssh-server git    # git is optional with `transfer: archive`; tar is already there
sudo systemctl enable --now ssh
```

Add your public key to `~/.ssh/authorized_keys` of the build user (`.ssh` mode `700`, the file `600`). A build user
created with `useradd` and no password cannot log in with one; to turn password logins off for every account, see
[TEAM-QUICKSTART.md, Linux worker](TEAM-QUICKSTART.md#linux-worker).

## Wake-on-LAN for a sleeping worker

Optional. If a PC sleeps or is shut down outside its `allowed_hours`, `lanq` can wake it with a magic packet (see
"Waking a sleeping worker" in the README for the `wake:` setting). Three things must be true for the packet to work, and
`lanq` can check none of them: it only sees that ssh does or does not answer later.

- **Wired Ethernet.** Wake-on-LAN usually works only over a cable. Wi-Fi rarely supports it.
- **BIOS/UEFI.** Enable the setting (often named "Wake on LAN", "Power On By PCI-E/PCI", "Resume by LAN" or similar).
  Turn off "ErP" or "deep sleep" power-saving options that cut power to the network card.
- **The network card driver and the OS** (below). The machine must be on the same LAN and broadcast domain as the PC
  running `lanq`, and its MAC address is the one of the wired adapter (`ipconfig /all` on Windows, `ip link` on Linux).

### Windows

1. Device Manager, Network adapters, your Ethernet adapter, Properties.
2. Power Management tab: tick **Allow this device to wake the computer** and **Only allow a magic packet to wake the
   computer** (the second one stops other network traffic from waking the PC at random).
3. Advanced tab: if present, set **Wake on Magic Packet** to Enabled.
4. **Fast Startup:** a PC shut down with Fast Startup enabled (the Windows default) is really hibernated and often does
   not wake from a magic packet, or does so unreliably. If it does not wake from a full shutdown, turn Fast Startup off
   (Control Panel, Power Options, Choose what the power buttons do, Change settings that are currently unavailable,
   untick Turn on fast startup). Waking from sleep usually works with it on.
5. Test before relying on it: shut the PC down, then run `lanq wake <machine> --wait` from your PC.

### Linux

1. Check support: `sudo ethtool eth0` should list `Supports Wake-on: ...g...`; `Wake-on: d` means it is off.
2. Turn it on: `sudo ethtool -s eth0 wol g` (use your adapter's name from `ip link`). This does not survive a reboot.
3. Make it persistent, one of:
   - NetworkManager: `nmcli connection modify "<connection name>" 802-3-ethernet.wake-on-lan magic`
   - systemd-networkd: in the interface's `.link` file under `[Match]`/`[Link]`, add `WakeOnLan=magic`
   - a small systemd unit running `ethtool -s eth0 wol g` after `network.target`.
4. Test as above with `lanq wake <machine> --wait`. `lanq doctor` shows the wake line and the same reminder.

## Toolchain in a non-standard place

A job finds its tools through the ssh session's `PATH`, which is not your login shell's. On some workers the tool on
that `PATH` is not the one the jobs need: a runtime-only .NET install in `C:\Program Files\dotnet` ("No .NET SDKs
were found") while the SDK lives in a private folder such as `D:\build\dotnet`, or a version manager (nvm, volta,
fnm) that only your interactive shell sets up.

`lanq doctor --remote` checks this for you. Its `dotnet` check (`dotnet --version`) fails without an SDK; doctor then
says when the dotnet on `PATH` has runtimes only ("dotnet 6.0.11 runtime only, no SDK") and searches the worker's usual
and private install folders (read-only, at most about five seconds): `$DOTNET_ROOT` and `~/.dotnet` everywhere; on Windows also `%ProgramFiles%\dotnet`,
`%LOCALAPPDATA%\Microsoft\dotnet` (the dotnet-install default), `%USERPROFILE%\.dotnet`, the registry's install
location, and `X:\dotnet` and `X:\<folder>\dotnet` on every fixed drive (one level deep, at most 64 folders a drive);
on Linux and macOS `/etc/dotnet/install_location`, `/usr/share/dotnet`, `/usr/lib/dotnet`, `/opt/dotnet`, the snap and
Homebrew folders. For the folder with the highest SDK it prints the fix, for example:

```text
FAIL  build-02 (remote): dotnet: 'dotnet --version' failed in the job environment (dotnet 6.0.11 runtime only, no SDK), but .NET SDK 10.0.102 is installed in D:/build/dotnet, a folder the ssh session's PATH does not reach
      fix: add `path_prepend: [D:/build/dotnet]` to build-02 in machines.yml (found .NET SDK 10.0.102 there), and `DOTNET_ROOT: D:/build/dotnet` under its env: so the SDK's tools find the runtimes next to it
```

A folder other local users could plant code in is never proposed: doctor and `lanq makeready --add` skip it with a
note ("not proposed for path_prepend: other local users can write there"). The folder, its `dotnet`, `sdk`, the newest
SDK, `host`, `shared` and their version folders must be owned by root (Administrators on Windows) or the build account
and writable by nobody else; every parent folder too, unless it is sticky. On Linux that is judged by the mode bits
and, since 0.9.2 (as lanq-makeready 0.4.1 does), by POSIX ACLs: a named `user:` or `group:` entry, or a `default:` one,
that lets another account write makes the folder open. lanq reads `getfacl -cpn` (at most 5 s); without `getfacl`, the
`+` ACL marker in `ls -ld` makes a folder open, and so does any group write with BusyBox `ls` (it shows no marker). If
`getfacl` is missing on a worker that uses ACLs, install the `acl` package there, or the folder stays "open". macOS is not
checked for ACLs. An agent reports such a note; it never changes a worker's permissions or ACLs to get a folder proposed.

Add those lines to the machine yourself (lanq never edits `machines.yml` or a worker's `PATH` by itself):

```yaml
  - name: build-02
    # ...
    path_prepend: [D:/build/dotnet]
    env:
      DOTNET_ROOT: D:/build/dotnet
```

## Windows workers where WMI is denied

On some Windows workers the build account (a normal user, not an administrator) is denied WMI/CIM, typically on a
PC tuned as a server: `Get-CimInstance` answers "Access denied", and so does `taskkill` (it uses WMI to find a process
tree). lanq works there without WMI (since 0.8.0):

- **Memory** comes from CIM when allowed, else from .NET (`Microsoft.VisualBasic.Devices.ComputerInfo`, which reads
  `GlobalMemoryStatusEx`). The probe says which (`mem_source=cim|vb`), so `--mem`, `size` routing and the memory hold
  keep working.
- **Load** comes from CIM when allowed, else from two `GetSystemTimes` samples a second apart (`load_source=native`).
  If neither works, `lanq health` shows `load ?` (JSON `loadKnown: false`) and load alone never holds the machine.
- **Stopping a job** (timeout, Ctrl+C, a stale slot, cleanup) does not need WMI: each job runs in a Windows job object
  that the job wrapper creates (with kill-on-close, so the whole tree also ends when the wrapper itself is killed or
  its ssh session drops), and lanq's stop and cleanup steps end that job object by name or walk the recorded
  processes' trees with a process snapshot. The helper that does this is written to `work_root/bin/` before each job.
  Processes a job leaves running (build servers, for example) end with the job.

`lanq doctor --remote` shows a `wmi` WARN for such a worker. Nothing has to change; if the machine's owner wants the
faster CIM probe, they can grant the account WMI read: `wmimgmt.msc`, WMI Control, Properties, Security, `Root\CIMV2`,
Security, then Enable Account and Remote Enable for the build user.

## What a job sees

Every job runs with `LANQ_JOB=<job id>` in its environment (on Linux and Windows), besides the machine's own `env:`
and `path_prepend:` (names starting with `LANQ_` are reserved, so `env:` cannot set them). A shard also gets
`LANQ_SHARD` and `LANQ_SHARDS`, and a job of a plan file (`lanq run --plan`) `LANQ_PLAN_JOB=<its name in lanq.yml>`.
A job run with `--env NAME=VALUE` (or `env:` in `lanq.yml`) also gets those variables, set after the machine's `env:`
and before the `LANQ_*` ones; only names in your `policy.allowed_env` get that far. They are in the job's process
environment, so other users of the worker can see them: they are for settings, never secrets.
A build script may read them, for example to tag its output. On Linux lanq also uses it to recognise the job's processes when it stops or cleans up a job whose group leader has already gone, so do not remove it from
the environment of long-running helper processes you want stopped with the job.

## A Linux worker in Docker on your own PC

No Linux machine on the network? `scripts/docker-linux-worker/` runs one in a container on your Windows PC (Docker
Desktop, Linux containers): Ubuntu 24.04 with the .NET 10 SDK (base image pinned by digest), git and sshd, key-only
login as the user `lanq` (no password, no sudo), `work_root` `/srv/lq` on a Docker volume.

```powershell
# PowerShell, on your PC (Docker Desktop running). Prints no key material.
scripts\docker-linux-worker\setup.ps1 -Name linux-01 -Port 2222 -KeyEnv LANQ_KEY_LINUX01 -Memory 8g -Cpus 4
# Then, yourself (it edits your lanq config): adds the entry and keeps it only when lanq doctor --remote passes for it.
scripts\docker-linux-worker\setup.ps1 -Name linux-01 -Port 2222 -KeyEnv LANQ_KEY_LINUX01 -Register
lanq makeready linux-01
```

What `setup.ps1` does (its header has the details):

- **Key.** A new ed25519 key `~\.ssh\lanq-linux-01_ed25519` without a passphrase. It has an owner-only ACL, and the user
  variable `LANQ_KEY_LINUX01` holds its path. `lanq health` warns about the missing passphrase. That is the trade-off:
  - the container is published only on `127.0.0.1`, so nothing else on the network can reach it;
  - the key line inside carries `restrict`: no pty and no forwarding of any kind.

  An existing key is reused, never overwritten.
- **Host key.** It is made on your PC, under `%LOCALAPPDATA%\lanq-docker-worker\<name>`, and mounted read-only. The
  container keeps the same host key across rebuilds, and it is known before the first login.
- **Limits.**
  - `--memory`/`--memory-swap` 8 GB;
  - `--cpus 4` with a `--cpuset-cpus` of the same size, the last 4 CPUs by default, so `nproc` inside says 4;
  - `--pids-limit`, `no-new-privileges` and `--restart unless-stopped`.
- **ssh.** It writes `~\.ssh\config.d\lanq-<name>`:
  - `HostName 127.0.0.1`, the port, `HostKeyAlias lanq-<name>`;
  - a private `UserKnownHostsFile` `~\.ssh\known_hosts.d\lanq-<name>`, with an owner-only ACL (Windows OpenSSH checks
    included files).

  It adds an `Include` line at the **top** of `~\.ssh\config` after a backup, then checks it with `ssh -G` and one
  login. The machine's `ssh:` is `lanq@lanq-<name>`, because lanq's `ssh:` has no port field.
- **`-Register`.** It is separate, and the owner runs it. For your `machines.yml`, and `machines.smoke.yml` when there
  is one, it:
  - backs the file up;
  - refuses a file it cannot edit safely: flow style, `machines:` not last, or the name already there;
  - appends the entry: `os: linux`, `capabilities: [dotnet, linux]`, `max_jobs: 1`, `transfer: git`, **`size:
    medium`**, and `DOTNET_CLI_TELEMETRY_OPTOUT`/`DOTNET_NOLOGO` under `env:`;
  - runs `lanq doctor --config <file> --remote --json`;
  - restores the backup unless the config loads and this machine has no failing check.

  It prints only "added" or "refused". Job settings belong under the machine's `env:`: sshd does not pass `docker -e`
  or the image's `ENV` on to a login.

What it proves and what it does not:

- **It proves:** your code builds and tests on Linux (glibc, case-sensitive paths, `/` separators, Linux line endings in
  tools) with the same lanq flow as a real worker. Jobs run with lanq's Linux scripts (`timeout`, `setsid`, `nice`,
  `ionice`) and the same `pgrep`/sshd checks.
- **It does not prove:** another distribution or an older glibc, other CPUs, real hardware, systemd services, a real
  network between machines, or anything that needs root or Docker inside the job.
- **It shares this PC.** A build in the container is a heavy job on your PC like any other. Do not start one while
  another heavy build or test run is going on here. That is why `max_jobs` is 1 and the size is declared `medium`. A
  declared `size:` wins over the probe, so `--min-size heavy` runs stay off it.
- **Load and memory are the VM's, not the container's.** Inside, `/proc/meminfo` and the load average describe Docker
  Desktop's whole VM (all its containers), not the 8 GB limit. `lanq health` and `lanq-makeready` therefore report the
  VM's memory (and makeready may suggest `heavy` and `max_jobs: 4`: ignore that; the declared values win). A job that
  goes over 8 GB is OOM-killed rather than held back. `nproc` honours only `--cpuset-cpus`, which is why the script
  sets it. The .NET GC follows the cgroup limit on its own.
- **makeready warnings you can ignore here:** "disk type: HDD" (the VM's virtual disk) and "no wired Ethernet adapter"
  (Wake-on-LAN does not apply).
- **Docker Desktop must be running.** It starts after you log in. While it is stopped, the worker shows as down (and a
  release smoke that lists it fails). The port goes through Docker Desktop's own proxy, not WSL's localhost forwarding.
- **Updates.** The base image is pinned by digest (a test checks it), so Dependabot does not move it. To update, see the
  comment at the top of the `Dockerfile`, then run `setup.ps1 -Force`. That replaces the container and keeps the
  volumes, the key and the host key.

**Host key changed?** If `%LOCALAPPDATA%\lanq-docker-worker\<name>` was deleted, the next `setup.ps1` makes a new host
key and rewrites the private known_hosts file. If you replaced the host key some other way, lanq reports that the host
key has CHANGED. In that case:

1. Check that nothing else is listening on the port.
2. Remove the old line with `ssh-keygen -R lanq-linux-01 -f ~\.ssh\known_hosts.d\lanq-linux-01`. Without `-f`,
   `ssh-keygen -R` looks only in `~\.ssh\known_hosts`, not in the private file.
3. Run `setup.ps1 -Force`.

To remove the worker:

1. Run `docker rm -f lanq-linux-01`.
2. Run `docker volume rm lanq-linux-01-work lanq-linux-01-nuget`.
3. Delete the Include line, `config.d\lanq-linux-01`, `known_hosts.d\lanq-linux-01`, the key, the user variable and
   the `machines.yml` entry.

## Then, on your PC

1. Make sure the worker's host key is in your `known_hosts` (connect once with `ssh` and compare the fingerprint
   with the one the PC's owner reads on the worker): lanq uses `StrictHostKeyChecking=yes`.
2. Add the worker to your `machines.yml` (`lanq init` writes a starter file):
   `lanq makeready --ssh build@10.20.30.21 --os windows --add --name micro-01`. It runs the read-only check and, only
   when the PC is READY, shows the entry (name, `os`, `ssh`, `work_root`, `capabilities`, `max_jobs`,
   `min_free_mem_gb`, `transfer`) and asks before adding it after your last machine; add `--key-env VAR` when you use
   a key file instead of ssh-agent. Then edit the entry if you need to (see `machines.example.yml`): optional
   `allowed_hours`, optional `transfer` (`git`, the default, or `archive` for a worker without git). For a PC someone
   else also uses, see the "family PC" example: hours by day, low priority (the default), a memory floor, heap caps in
   `env:`, and a `PAUSE` file the people at that PC can create to hold it. If a tool installed per user (nvm, pyenv,
   rustup, sdkman) works in your interactive shell but `lanq` jobs cannot find it, list its folder under
   `path_prepend:`.
3. Run `lanq doctor --remote`: it checks your side (ssh, known_hosts, key) and, on each worker, that `work_root` is
   writable, that git or tar is there, and that each capability's tool (`dotnet`, `node`, `npm`, `python`, `go`,
   `rust`, `java`, `docker`) answers in the same non-login shell and environment a job gets. A tool that works when you
   log in but fails here needs its folder in `path_prepend:`.
4. Run `lanq health`: the worker should show as ready, or as "hold short" with the reason. `lanq health --needs
   <caps>` also says whether it could take a job with those capabilities.

If a `lanq` process was killed mid-job, its slot on the worker counts as stale 30 minutes after the slot's deadline: it no longer
makes the machine busy (`lanq health` shows `(1 stale: reclaimed by the next claim)`), and the next claim reclaims it.
`lanq unlock <name>` clears expired slots at once; `lanq unlock <name> --all --yes` clears every slot (including ones
left by a lanq before 0.4) when you are sure nothing is running there.

## For AI agents reading this

- You may **use** workers through `lanq run --needs <capabilities> -- <allowlisted command>`; see
  `docs/agents/`.
- You must **not** set up workers, edit `machines.yml`, change allowlists, install software on workers, or handle
  SSH keys: that is the developer's job. Do not run `lanq makeready` or `lanq unlock` unless the developer asks. If a job fails because a worker lacks a tool, report which capability is
  missing instead of trying to fix the worker.
