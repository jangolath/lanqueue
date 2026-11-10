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
A build script may read them, for example to tag its output. On Linux lanq also uses it to recognise the job's processes when it stops or cleans up a job whose group leader has already gone, so do not remove it from
the environment of long-running helper processes you want stopped with the job.

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

If a `lanq` process was killed mid-job, its slot on the worker is reclaimed 30 minutes after the slot's deadline.
`lanq unlock <name>` clears expired slots at once; `lanq unlock <name> --all --yes` clears every slot (including ones
left by a lanq before 0.4) when you are sure nothing is running there.

## For AI agents reading this

- You may **use** workers through `lanq run --needs <capabilities> -- <allowlisted command>`; see
  `docs/agents/`.
- You must **not** set up workers, edit `machines.yml`, change allowlists, install software on workers, or handle
  SSH keys: that is the developer's job. Do not run `lanq makeready` or `lanq unlock` unless the developer asks. If a job fails because a worker lacks a tool, report which capability is
  missing instead of trying to fix the worker.
