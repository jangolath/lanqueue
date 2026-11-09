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

Either way, lanq never changes a worker: every fix it suggests is for the PC's owner to run on that PC.

## Checklist

| Need | Required? | Why | How to check on the worker |
|---|---|---|---|
| SSH server accepting your public key | **Required** | every lanq call is key-only SSH | from your PC: `ssh -o BatchMode=yes <user>@<worker> echo ok` |
| Git | **Recommended; required for the default `transfer: git` mode.** Optional with `transfer: archive` | git mode unpacks the bundle into a throwaway worktree | `git --version` |
| tar (+ gzip on Linux) | Only with `transfer: archive`. On Linux must be **GNU tar or bsdtar**; on Windows the **built-in bsdtar** (`%SystemRoot%\System32\tar.exe`, Windows 10 1803+ / Server 2019+). **busybox tar (Alpine, many minimal images) is refused**: `lanq health` holds the machine as unsupported | archive mode extracts the `tar.gz` into a fresh job folder; lanq trusts the pre-extraction listing only from these tars | `tar --version` must say `GNU tar` or `bsdtar` (Windows: `%SystemRoot%\System32\tar.exe --version` says `bsdtar`); `command -v gzip` on Linux |
| Shell | **Required** | Windows: Windows PowerShell (built in). Linux: `sh` | — |
| `base64`, `sha256sum`, `find`, `awk`, `sort`, `readlink` (Linux) | Only for `lanq run --fetch` | the fetch script lists the files and sends them back as checksummed text. Standard on every distribution (coreutils, findutils; busybox has them too); Windows needs nothing extra | `command -v base64 sha256sum find awk sort readlink` |
| Writable work folder (`work_root`) with free disk | **Required** | each job gets its own worktree or folder there; `lanq health` holds a machine with low disk | `lanq health` from your PC |
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
sudo apt install openssh-server git    # git is optional with `transfer: archive`; tar is already there
sudo systemctl enable --now ssh
```

Add your public key to `~/.ssh/authorized_keys` of the build user (`chmod 600`).

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

## Then, on your PC

1. Add the worker to your `machines.yml` (see `machines.example.yml`): name, `os`, `ssh` target, `work_root`,
   `capabilities`, `max_jobs`, optional `allowed_hours`, optional `transfer` (`git`, the default, or `archive` for
   a worker without git). For a PC someone else also uses, see the "family PC" example: hours by day, low priority
   (the default), a memory floor, heap caps in `env:`, and a `PAUSE` file the people at that PC can create to hold it.
   If a tool installed per user (nvm, pyenv, rustup, sdkman) works in your interactive shell but `lanq` jobs cannot
   find it, list its folder under `path_prepend:`.
2. Make sure the worker's host key is in your `known_hosts` (connect once with `ssh` and verify the fingerprint):
   lanq uses `StrictHostKeyChecking=yes`.
3. Run `lanq doctor --remote`: it checks your side (ssh, known_hosts, key) and, on each worker, that `work_root` is
   writable, that git or tar is there, and that each capability's tool (`dotnet`, `node`, `npm`, `python`, `go`,
   `rust`, `java`, `docker`) answers in the same non-login shell and environment a job gets. A tool that works when you
   log in but fails here needs its folder in `path_prepend:`.
4. Run `lanq health`: the worker should show as ready, or as "hold short" with the reason.

If a `lanq` process was killed mid-job, its slot on the worker is reclaimed 30 minutes after the slot's deadline.
`lanq unlock <name>` clears expired slots at once; `lanq unlock <name> --all --yes` clears every slot (including ones
left by a lanq before 0.4) when you are sure nothing is running there.

## For AI agents reading this

- You may **use** workers through `lanq run --needs <capabilities> -- <allowlisted command>`; see
  `docs/agents/`.
- You must **not** set up workers, edit `machines.yml`, change allowlists, install software on workers, or handle
  SSH keys: that is the developer's job. Do not run `lanq makeready` or `lanq unlock` unless the developer asks. If a job fails because a worker lacks a tool, report which capability is
  missing instead of trying to fix the worker.
