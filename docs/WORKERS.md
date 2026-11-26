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

  An existing key is reused, never overwritten. An existing `LANQ_KEY_LINUX01` that points elsewhere is never
  changed unless you add `-ReplaceKeyEnv` (`-Force` does not).
- **Host key.** It is made on your PC, under `%LOCALAPPDATA%\lanq-docker-worker\<name>`, and mounted read-only under
  `/root` (0700) in the container, so job code running as `lanq` cannot read it. The container keeps the same host key
  across rebuilds, and it is known before the first login.
- **Limits.**
  - `--memory`/`--memory-swap` 8 GB;
  - `--cpus 4` with a `--cpuset-cpus` of the same size, the last 4 CPUs by default, so `nproc` inside says 4;
  - `--pids-limit`, `no-new-privileges`, `--init` and `--restart unless-stopped`;
  - `--cap-drop ALL`, keeping only the six capabilities sshd and the entrypoint need: CHOWN, DAC_OVERRIDE, FOWNER,
    SETUID, SETGID and SYS_CHROOT.
- **ssh.** It writes `~\.ssh\config.d\lanq-<name>`:
  - `HostName 127.0.0.1`, the port, `HostKeyAlias lanq-<name>`;
  - a private `UserKnownHostsFile` `~\.ssh\known_hosts.d\lanq-<name>`, with an owner-only ACL (Windows OpenSSH checks
    included files).

  It adds an `Include` line at the **top** of `~\.ssh\config` after a backup. The line goes in front of the file's
  bytes as they are, and the file's ACL is not changed; a UTF-16 file is refused. It then checks the result with
  `ssh -G` and one login, and prints the steps to undo the setup when either fails. The machine's `ssh:` is `lanq@lanq-<name>`, because lanq's `ssh:` has no port field.
- **`-Register`.** It is separate, and the owner runs it. For your `machines.yml`, and `machines.smoke.yml` when there
  is one, it:
  - checks every file first, and refuses them all when one cannot be edited safely: flow style, `machines:` not last,
    or the name already there;
  - backs each file up;
  - appends the entry: `os: linux`, `capabilities: [dotnet, linux]`, `max_jobs: 1`, `transfer: git`, **`size:
    medium`**, and `DOTNET_CLI_TELEMETRY_OPTOUT`/`DOTNET_NOLOGO` under `env:`;
  - runs `lanq doctor --config <file> --remote --json`, for 5 minutes at most;
  - restores **every** file from its backup unless each config loads and this machine has no failing check.

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
   the entry in `machines.yml` (and `machines.smoke.yml`).
4. Delete the host key folder `%LOCALAPPDATA%\lanq-docker-worker\linux-01`.
5. Delete the backups once you no longer need them: `~\.ssh\config.bak-*` and the `machines*.yml.bak-*` files next to
   your lanq config.

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

## Memory per job (`job_mem_gb`, 0.13.0)

A job that does not say how much memory it needs reserves nothing: only `max_jobs` limits a machine. Give a machine
`job_mem_gb: N` (or the pool `policy.job_mem_gb: N`): about how much memory one job uses there. A job's need on that
machine is the larger of its `--mem` (lanq.yml `mem:`) and `N`; the machine keeps its `min_free_mem_gb` free, and it
runs at most (total memory - floor) / `N` jobs at once, a cap every capped slot claim enforces (it counts every live
slot, so a capped run never adds a job above the cap, even next to runs without it; a run without the cap, from another
`--config` or an older lanq, can still fill `max_jobs`). `lanq doctor --remote` shows the arithmetic next to
`max_jobs`. A "16 GB" machine reports about 15.9 GB, so a `job_mem_gb` close to total minus floor rules every job out
there (`mem_exceeds_total`).

Nothing is reserved between jobs: a plan's sibling jobs, the shards of one run and parallel lanq runs each see the
machine's free memory as the probe reports it, and a job that has claimed its slot but not yet allocated its memory is
invisible to the next claim. The job size's cap bounds how many can start at once; within the cap, give the floor
(`min_free_mem_gb`) some room for that.

## Hygiene for .NET workers (`hygiene:`, 0.13.0)

```yaml
    hygiene: [dotnet]        # or [dotnet-lean]
```

- `dotnet`: `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOTNET_NOLOGO=1` for the machine's jobs.
- `dotnet-lean`: the same plus `UseSharedCompilation=false`.

Trade-off: node reuse off costs a few seconds per build (new MSBuild nodes each time). `dotnet-lean` makes every build
start the compiler cold: expect builds 10-40% slower on large solutions, in exchange for no resident compiler server.
Choose `dotnet-lean` only on machines where memory, not CPU, is the limit. A repository's own
`<UseSharedCompilation>true</UseSharedCompilation>` wins over the environment, so `dotnet-lean` is best effort.

Why it matters with `max_jobs` above 1: reusable MSBuild nodes and the compiler server are shared by every build of the
same account. When the job that started one ends, its cleanup (the Windows job object, the Linux sweep) stops it, and
a sibling job that was using it can fail (`MSB4166: child node exited prematurely`). `lanq doctor` warns about a
`dotnet` machine with `max_jobs` above 1 and no `hygiene:`. On Linux, cleanup also stops every process of the build
user that carries exactly `LANQ_JOB=<this job>` (a server that left the job's process group included). It judges each
process by its own command line, never by the machine's config: an MSBuild node goes only when it was started with
`nodeReuse:false` (as `hygiene:` makes it, unless a machine `env:`, a job's `--env` or the repository turns reuse back
on); the MSBuild server (`nodemode:8`), the compiler server (`VBCSCompiler`) and the Razor build server of older SDKs
(`rzc.dll server`) are always spared, since a sibling may use them and they end on their own idle timeout. lanq never
runs `dotnet build-server shutdown`: it would stop every
build server of the account, a sibling slot's included. On Windows, cleanup relies on the job object; when PowerShell
there cannot load lanq's helper (constrained language), `lanq doctor --remote` warns `leftover processes possible (no
job object)`.

## Using this PC as a fallback (0.13.0)

Off by default. Describe this computer as one entry with `fallback: true` and `local: true` (instead of `ssh:`), then
choose a policy:

```yaml
policy:
  fallback: off            # off (default) | on | ask
machines:
  - name: dev-pc
    os: windows            # must be this computer's OS; otherwise only the fallback is off here
    local: true
    fallback: true
    work_root: C:/lq-fallback
    capabilities: [dotnet, windows]
    max_jobs: 1
```

- It is never an ordinary worker: no placement, `--shards auto` or plain `lanq health` sees it. `lanq health --on
  dev-pc`, `lanq unlock dev-pc` and `lanq prune dev-pc` name it on purpose (a stuck slot here needs a tool too). It is
  used only where a job would otherwise end NoCapacity (after the wait budgets, or at once when no pool machine can
  ever take the job). With `on` or `ask` and no wait flag given, a run waits 120 s for a pool slot first. Where the
  fallback cannot act (`ask` with no terminal, or a refusal below), the run behaves exactly as with `off`: no 120 s
  wait, and a job no pool machine can ever take ends at once. A refusal, and `--fallback on|ask` with no usable fallback
  entry, is said once on stderr at the start.
- The 120 s floor is a slot wait only. **Wake before fallback:** when pool machines that could take the job look asleep
  and have a `wake:` setting (with the job's capabilities and size, inside their `allowed_hours`, as for any automatic
  wake), a run that may fall back first sends each of them one Wake-on-LAN packet, all at once (`[lanq] ... sent
  Wake-on-LAN packets to build-02, build-03 (waiting up to 120 s for them before falling back to this computer)`; under
  `ask`, "before asking to use this computer"). Then it waits **one shared wait of up to 120 s, counted from the first
  packet**. The wait ends early only when the job is placed, or when every woken machine is ruled out for good or
  paused. A woken machine that answers ssh but is held, as Windows often is just after boot (load or memory pressure
  from its own startup, an unreadable probe), keeps the wait going until the 120 s are up; one that has not answered
  stops counting once its own `wake.wait_seconds` have passed. If a woken machine can take the job, it runs there; if
  not, the job falls back, and the fallback line, the `ask` question, the NOT RUN message, JSON `wakes` (one entry per
  machine) and the next steps say what each wake did (`build-02 woken, no ssh answer within 60 s`). Because the
  sleepers are woken together, a machine with Wake-on-LAN broken costs at most that one 120 s per run, not 120 s per
  sleeper; each machine is woken at most once per run, and later jobs of the run (plan jobs, a baseline rerun) that
  could use the same machines do not wait again once that wait is over. A job waits only for wakes on machines it could
  use. A machine **known to be paused** (awake, with its `PAUSE` file) is never woken; one paused while asleep cannot
  say so and may be woken, but once it answers as paused it no longer delays the fallback. A sleeper without `wake:`
  never delays it either. With `ask`, lanq wakes first and asks only if the job still needs this computer. This applies
  when lanq chooses the eligible wait: no `--wait` and no `--wait-eligible` (`--wait-slot` alone keeps it).
  `--wait-eligible S` or `--wait S` decide instead, as for any run: above 0 one sleeper at a time is woken inside that
  budget, 0 never wakes. `--no-wake` never wakes; with the fallback `off` nothing changes. `--wait-eligible` alone sets
  no slot floor, so a busy pool falls back at once.
- The wake comes when no awake machine can take the job (or, for a shard set, when no new shard could start this look),
  not only just before the fallback: a shard set whose capable machines are all full, or a single job waiting for a
  slot, may therefore wake a sleeper for extra capacity while it waits, as with `--wait` above 0. `--no-wake` avoids that.
- `lanq run --dry-run` says when the run would fall back (`Would fall back: ...`, JSON `wouldFallBack: "dev-pc"`), and
  which sleepers it would wake (`Would wake: build-02, build-03 (... before falling back to this computer)`, JSON
  `wouldWake`, the first by name, and `wouldWakeAll`). Under `ask` it predicts as the real run would where that run
  could ask (a terminal, no `--json`, not CI or an agent).
- `lanq run --fallback off|on|ask` overrides the policy for one run (also with `--plan`). A lanq.yml job's
  `fallback: off` wins over both; lanq.yml may not say `on` or `ask` (a repository never chooses to run on your PC).
  `--on <worker>` never falls back; `--on dev-pc` is refused.
- `ask` prompts `[lanq] lint: no pool slot. Run it on this computer (dev-pc)? A yes also covers the remaining jobs in
  this run, each only after its own wait runs out. [y/N]` only when stdin and stderr are a terminal and there is no
  `--json`, `CI`, `TERM=dumb`, `LANQ_NONINTERACTIVE` or coding agent; elsewhere it acts as `off` and says so. As the
  question says, a yes covers the rest of that run, but it never cuts another job's wait short: each job, shard set or
  baseline rerun still goes to this computer only once its own wait budgets have run out (or at once when no pool
  machine can ever take it). A prompt with no answer in 120 s is a no, and the run asks no more. lanq reads the
  terminal only from the first prompt on.
- A job, shard set or plan job that fell back may use this computer's slots (`max_jobs`, memory), always after every
  free pool slot; another lanq run that finds those slots busy ends NOT RUN.
- It runs the same scripts a worker gets, without sshd, in a job object that ends with lanq on Windows. Its processes
  start in the fallback's `work_root` (never in your repository), with no console of their own, and Windows
  PowerShell by its full `System32` path. If a process cannot join lanq's job object, lanq says so once (best effort).
  On Linux, Ctrl+C stops the wrapper and cleanup stops the job; if the terminal is closed instead, **the job keeps
  running on your PC** until it ends or reaches its timeout, and its slot then goes stale as on a worker (`lanq unlock
  dev-pc` frees it once it is stale).
- **What the clean environment does and does not do.** The job's environment is built from a short allowlist of
  variables, so none of your shell's variables (`SSH_AUTH_SOCK`, `GH_TOKEN`, cloud credentials, `NUGET_*`,
  `MSBuild*`, an outer `LANQ_JOB`) are used by accident. That is all it does. The job still runs as you: repository
  code can reach your ssh agent (on Windows the fixed `\\.\pipe\openssh-ssh-agent`, used without any variable), Git
  Credential Manager and the Windows Credential Manager, your keyring and the sockets under `/run/user/<uid>` (agent,
  gpg-agent, D-Bus), your home folder, browser profile and keys on disk, like any program you start. lanq does not
  sandbox them. That is why the fallback is off by default: turn it on only for repositories whose code you would run
  yourself.
- The allowlist also leaves out system-wide settings a worker's login environment may have (`JAVA_HOME`, a user-local
  `DOTNET_ROOT`, `HTTP(S)_PROXY`, `NO_PROXY`, `PSModulePath`). Set what your builds need in the fallback entry's `env:`,
  or a build that passes on the pool can fail here and look like a code failure.
- lanq refuses to use it when it runs elevated or as root (`fallback_refused`; next step: run lanq from a terminal
  without those rights), or when `work_root` and the repository overlap (`fallback_work_root`; the repository is its top
  folder, links followed; on Windows also subst drives, 8.3 short names and volume mount points, by every name Windows
  knows the folder by). Linux bind mounts are not followed: do not bind-mount the repository under `work_root`. Either
  way the run then acts as `off`.
- Results say so: `[lanq] running on this computer (fallback: no pool slot)` (or `no pool machine can take it`), the
  status line `(N of M jobs on this computer, fallback)`, JSON `fallback` and `fallbackJobs`, jobs.jsonl `fallback`,
  `lanq stats` "(this computer)". Such a run is not pool evidence: rerun on the pool to confirm. A baseline verdict where
  one side ran here says so, and `--shards auto --target-minutes` timing leaves fallback runs out.

## The `post:` step (0.13.0)

lanq.yml `post: [dotnet, build-server, status]` (or `lanq run --post "dotnet build-server status"`) runs one more
allowlisted command after the job: after its fetch, before its cleanup, in the same slot and folder, with its own pid
file and a 5-minute limit (less when the machine's allowed hours plus grace end sooner; under 30 s left, it is skipped).
It runs after Completed, Failed and TimedOut; its exit code is a warning only, and a step that exits 124 itself is not
called timed out. The step ends at its own exit, even when a process it started keeps the output open. Ctrl+C,
`plan_timeout` or `--fail-fast` during the step end only the step (`post.cancelled: true`): the job keeps its own
status, exit code, fetched files and results. `--log-tail` ends where the step's output begins, so it shows the job's
own last lines. A hook acts with the account's full reach on the worker. To stop build servers, use `hygiene:`
instead: a hook that stops them stops a sibling slot's too.

## Cleaning up leftovers (`lanq prune`, 0.13.0)

A crashed orchestrator or a dropped connection can leave `jobs/<id>`, `incoming/<id>.*` and pid files behind, and
repository caches grow. `lanq prune build-01` (or `--all`) lists what it would remove, per kind with sizes; add `--yes`
to remove it. It removes only entries named like lanq's job ids, older than `--older-than` (default `2d`), never one a
slot file (live or stale: `lanq unlock` decides) or a running process names. `--caches` adds repository caches unused
for 30 days (or `--older-than`, if larger); every cache step runs only while no slot is in use, holding every slot.
It refuses a `work_root` at a drive or filesystem root, a share root (`\\server\share`) or your home folder (`lanq
doctor` warns about such a `work_root` for every machine), one without `locks/`, and one whose `jobs`, `incoming`,
`locks` or `repos` is a link, and it removes a link as a link without following it (a `work_root` that is itself a
link is resolved from its own folder). A listing with `--caches` counts a cache whose only worktrees belong to jobs
the same listing names as one that would go. A paused machine or
one outside its hours is skipped unless `--force`.

## For AI agents reading this

- You may **use** workers through `lanq run --needs <capabilities> -- <allowlisted command>`; see
  `docs/agents/`.
- You must **not** set up workers, edit `machines.yml`, change allowlists, install software on workers, or handle
  SSH keys: that is the developer's job. Do not run `lanq makeready` or `lanq unlock` unless the developer asks.
  `lanq prune` without `--yes` only lists and is fine to run; never add `--yes`, `--force` or `--fallback on|ask`
  unless the developer said so. If a job fails because a worker lacks a tool, report which capability is
  missing instead of trying to fix the worker.
