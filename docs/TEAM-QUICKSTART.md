# Team quick start: one orchestrator, a mixed pool of workers

This guide sets up `lanq` for a small team in one evening: **one orchestrator** (the PC people run `lanq` from) and
a pool of **workers**, a mix of Windows 10/11 and Linux PCs, including low-RAM micro PCs and family PCs that are only
free at night. Every command and flag below exists in `lanq` as shipped; where a step happens on a worker, it is the
PC owner's job (`lanq` never changes a worker).

Throughout, the example addresses are `10.20.30.x` and the build user is `lanqbuild`. Use your own.

1. [Install lanq on the orchestrator](#1-install-lanq-on-the-orchestrator)
2. [Create a key and a starter machines.yml](#2-create-a-key-and-a-starter-machinesyml)
3. [Set up each worker](#3-set-up-each-worker) ([Windows 10/11](#windows-1011-worker), [Linux](#linux-worker))
4. [Connect once and run lanq makeready on each worker](#4-connect-once-and-run-lanq-makeready-on-each-worker)
5. [Fill in machines.yml](#5-fill-in-machinesyml)
6. [Check everything with lanq doctor --remote](#6-check-everything-with-lanq-doctor---remote)
7. [Run a first job](#7-run-a-first-job)
8. [Sizing micro PCs](#8-sizing-micro-pcs)
9. [Overnight family PCs](#9-overnight-family-pcs)
10. [Troubleshooting: the ten errors you are most likely to see](#10-troubleshooting-the-ten-errors-you-are-most-likely-to-see)

## 1. Install lanq on the orchestrator

The orchestrator needs `ssh`, `scp` and `git` on the PATH (Windows 10/11 ship the OpenSSH client; install
[Git for Windows](https://git-scm.com/download/win) if `git --version` fails). Then pick one:

```bash
# .NET tool (needs the .NET 10 SDK)
dotnet tool install --global LanQueue

# Windows, with Scoop (no .NET needed)
scoop install https://github.com/jangolath/lanqueue/releases/latest/download/lanq.json

# Native binary (no .NET needed): download lanq-<os>-<arch> from the GitHub release, verify it, put lanq on your PATH
gh release download --repo jangolath/lanqueue --pattern 'lanq-linux-x64.tar.gz' --pattern SHA256SUMS
sha256sum --check --ignore-missing SHA256SUMS
```

`lanq --version` confirms it works. Details and every download: [README, Install](../README.md#install).

## 2. Create a key and a starter machines.yml

lanq logs in with a key only; there is no password option. Make one key for the orchestrator:

```bash
ssh-keygen -t ed25519 -f ~/.ssh/lanq_ed25519 -C lanq          # PowerShell: -f $HOME\.ssh\lanq_ed25519
```

Give it a passphrase and load it into ssh-agent (`ssh-add ~/.ssh/lanq_ed25519`; on Windows start the agent once in
an elevated PowerShell with `Get-Service ssh-agent | Set-Service -StartupType Automatic; Start-Service ssh-agent`).
lanq then uses the agent and you leave `key_env` out of the config. The alternative is `key_env:` naming an
environment variable that holds the key file's path; `lanq health` warns when that file has no passphrase. See
[Keeping your keys safe](../README.md#keeping-your-keys-safe).

Now write the starter config:

```bash
lanq init                                   # writes ./machines.yml; never overwrites a file
lanq init --config ~/.config/lanq/machines.yml    # or straight to the per-user path lanq reads by default
```

On Windows the per-user path is `%APPDATA%\lanq\machines.yml`. A config in the current folder is **never** read by
itself: either pass `--config FILE` on every command, set `LANQ_CONFIG`, or keep the file at the per-user path. The
starter has one live example machine (`example-01`, which you will replace), a commented Windows micro PC and a
commented Linux family PC, and policy defaults for small PCs (`min_free_mem_gb: 1.5`, `max_jobs: 1`).
`lanq init --json` prints one object (`status`, `exitCode`, `path`, `message`).

## 3. Set up each worker

Each worker needs an SSH server that accepts your public key, git (or only tar with `transfer: archive`) and the
toolchain your jobs use (for example the .NET 10 SDK). The full checklist is [WORKERS.md](WORKERS.md). Use a
**dedicated, non-admin build user** on every worker: it is simpler (see the Windows trap below) and a job can then
touch only that user's files.

Your public key is the one line in `~/.ssh/lanq_ed25519.pub` on the orchestrator.

### Windows 10/11 worker

In an **elevated** PowerShell on the worker:

```powershell
# 1. OpenSSH Server (an optional Windows feature), started now and at every boot
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
Start-Service sshd
Set-Service -Name sshd -StartupType 'Automatic'

# 2. The firewall: installing the feature normally adds this rule; check it is there and enabled
Get-NetFirewallRule -Name *OpenSSH-Server* | Select-Object Name, Enabled, Profile
# If it is missing:
New-NetFirewallRule -Name sshd -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22

# 3. A non-admin build user; log in once so Windows creates its profile folder
New-LocalUser -Name lanqbuild -Password (Read-Host -AsSecureString -Prompt 'Password for lanqbuild')
runas /user:lanqbuild cmd        # type the password, then close the new window

# 4. Your public key for that user, readable only by the user, SYSTEM and Administrators
$ssh = 'C:\Users\lanqbuild\.ssh'
New-Item -ItemType Directory -Force $ssh | Out-Null
Set-Content -Path "$ssh\authorized_keys" -Value 'ssh-ed25519 AAAA...your key... lanq' -Encoding ascii
icacls "$ssh\authorized_keys" /inheritance:r /grant 'lanqbuild:F' /grant 'SYSTEM:F' /grant 'Administrators:F'

# 5. A work folder the build user can write
New-Item -ItemType Directory -Force C:\lanq | Out-Null
icacls C:\lanq /grant 'lanqbuild:(OI)(CI)M'
```

**The `administrators_authorized_keys` trap.** For an account in the local Administrators group, Windows OpenSSH
ignores `C:\Users\<user>\.ssh\authorized_keys` and reads only `C:\ProgramData\ssh\administrators_authorized_keys`,
which must be readable only by Administrators and SYSTEM
(`icacls C:\ProgramData\ssh\administrators_authorized_keys /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F'`).
A key in the wrong file gives `Permission denied (publickey)` with no other hint. A non-admin build user avoids the
trap altogether.

Then install the toolchain machine-wide (for example the .NET 10 SDK) and, if the PC should run jobs at night, see
[section 9](#9-overnight-family-pcs). `tar` is built into Windows 10 1803 and later, so `transfer: archive` works
without git.

### Linux worker

Debian/Ubuntu shown; use your distribution's package manager elsewhere.

```sh
sudo apt install openssh-server git              # git is optional with transfer: archive
sudo systemctl enable --now ssh
sudo useradd -m -s /bin/bash lanqbuild           # a dedicated build user, no sudo
sudo install -d -m 700 -o lanqbuild -g lanqbuild /home/lanqbuild/.ssh
echo 'ssh-ed25519 AAAA...your key... lanq' | sudo tee /home/lanqbuild/.ssh/authorized_keys >/dev/null
sudo chown lanqbuild:lanqbuild /home/lanqbuild/.ssh/authorized_keys
sudo chmod 600 /home/lanqbuild/.ssh/authorized_keys
sudo install -d -o lanqbuild -g lanqbuild /srv/lanq   # the work_root
sudo ufw allow OpenSSH                           # only if ufw is active
```

Install the toolchain. A tool installed per user (for example .NET with `dotnet-install.sh` into `~/.dotnet`, or
nvm) is not on the PATH of a non-login ssh session: list its folder under `path_prepend:` for that machine.

## 4. Connect once and run lanq makeready on each worker

lanq connects only to hosts already in your `known_hosts` (`StrictHostKeyChecking=yes`), so connect once by hand and
check the fingerprint with the PC's owner:

```bash
ssh lanqbuild@10.20.30.21          # answer yes after checking the fingerprint, then type exit
```

The owner can read the fingerprint on the worker with `ssh-keygen -lf C:\ProgramData\ssh\ssh_host_ed25519_key.pub`
(Windows) or `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` (Linux).

Then run the read-only readiness check from the orchestrator. The script is built into lanq and goes over ssh stdin;
nothing is written to the worker:

```bash
lanq makeready --ssh lanqbuild@10.20.30.21 --os windows
lanq makeready --ssh lanqbuild@10.20.30.31 --os linux
```

It prints verdicts (`worker_git`, `worker_archive`, `dotnet10`, `docker`, `overnight`), every WARN or FAIL check with
advice for the PC's owner, and a suggested `machines.yml` entry. Exit `0` means at least one transfer mode works.

## 5. Fill in machines.yml

Paste each suggested entry under `machines:`, delete `example-01`, and confirm every value: a unique `name`, `os`,
`ssh`, `work_root` (Windows: a drive path with forward slashes such as `C:/lanq`), `capabilities` (the tags jobs ask
for with `--needs`, such as `dotnet`, `node`, `windows`, `linux`), `max_jobs`, and `transfer`. Add the commands your
team runs to `policy.allowed_commands` (a job's command must start with one of them) and the folders to
`policy.allowed_directories`. Every key is explained in [machines.example.yml](../machines.example.yml).

## 6. Check everything with lanq doctor --remote

```bash
lanq doctor --remote --config ./machines.yml      # leave --config out if the file is at the per-user path
```

It checks your side without any network (ssh, scp, git, ssh-keygen, the config, a `known_hosts` entry and the key
for every machine) and then, over ssh, each worker's `work_root`, git or tar, and each capability's tool **in the job
environment**. Every line is `OK`, `WARN`, `FAIL`, `SKIP` or `HOLD`; exit `0` when nothing is `WARN` or `FAIL`. Then:

```bash
lanq health                 # which machines are free right now
lanq makeready --all        # the readiness table for the whole pool (add --json for scripts)
```

`lanq makeready --all` scans four machines at a time with the same rules as `lanq makeready <machine>` (a paused
machine or one outside its `allowed_hours` is `held` unless you add `--force`) and prints one row per machine:

```text
MACHINE    OS       VERDICT      TOP PROBLEM
micro-01   linux    WARN 1       Pending reboot: a restart is pending
micro-02   windows  FAIL 1       OpenSSH Server: sshd
family-pc  windows  held         paused (a PAUSE file is in its work_root); --force scans anyway
micro-03   linux    unreachable  ssh: connect to host 10.20.30.33 port 22: Connection timed out
```

Exit `0` when every machine is OK, `1` when any has a WARN or FAIL, otherwise `70` when any was unreachable (or sent
an unusable report, verdict `error`), `75` when any was held. `lanq makeready <machine>` shows the details and advice.

## 7. Run a first job

From inside a git repository on the orchestrator:

```bash
lanq run --needs dotnet --wait 120 -- dotnet test
```

lanq picks the least loaded machine that has every `--needs` capability, ships your current git state (uncommitted
changes included) to a throw-away folder, runs the command, streams the log back and cleans up. The exit code is the
job's own; `2` means refused by policy or bad arguments, `70` an infrastructure error, `75` no eligible machine and
`124` a timeout or the end of the machine's hours. Useful flags: `--dir tests` (where the command runs), `--mem 2` (only
machines with 2 GB available), `--timeout 60` (minutes), `--fetch 'TestResults/**/*.trx'` (bring results back),
`--json` (one result object for scripts and coding agents). `lanq stats` sums the time each machine spent on jobs.

## 8. Sizing micro PCs

A micro PC with 4 to 8 GB of RAM can build and test, one job at a time:

- **`max_jobs: 1`.** Two .NET builds at once on 8 GB swap or fail.
- **Memory holds.** `policy.min_free_mem_gb` holds every machine below that much available memory; set a higher
  `min_free_mem_gb:` on the smallest PCs (2 is a good start). On Linux, `max_mem_pressure` also holds a machine that
  is stalled on memory. A job that needs more asks for it with `lanq run --mem 4`.
- **Cap the heaps** in the machine's `env:` (job only): `DOTNET_GCHeapHardLimitPercent: "0x32"` (.NET heap at most
  50% of RAM, hexadecimal), `MSBUILDNODECOUNT: "1"` (one MSBuild node), `NODE_OPTIONS: "--max-old-space-size=2048"`.
- **Transfer mode.** `transfer: git` (the default) keeps a cache on the worker, so later jobs send only new commits:
  the best choice when git is installed. `transfer: archive` needs only tar, sends the whole snapshot each time and
  extracts it into a fresh folder: fine for small repositories and for PCs where git cannot be installed.
- **Disk.** `policy.min_free_disk_gb` (default 5) holds a machine whose `work_root` drive is nearly full.
- Jobs run at **low priority** by default (`priority: low`), so a PC someone is using stays responsive.

## 9. Overnight family PCs

A PC someone uses during the day can still work at night:

- **`allowed_hours`**, on the worker's own clock, by day: `["Mon-Fri 22:00-07:00", "Sat,Sun 01:00-09:00"]`. lanq
  starts nothing when fewer than `policy.min_window_minutes` (default 10) are left and stops a job
  `policy.window_grace_minutes` (default 15) after the window ends (exit `124`, `[lanq] WindowClosed`).
- **A `PAUSE` file** in its `work_root` (Command Prompt: `type nul > C:\lanq\PAUSE`; Linux: `touch /srv/lanq/PAUSE`)
  holds the machine until someone deletes it. A job already running is not affected.
- **Wake-on-LAN**, for a PC that sleeps: give it `wake: {mac: "aa:bb:cc:dd:ee:ff"}` (optional `broadcast:` such as
  `10.20.30.255` and `wait_seconds:`). `lanq wake <machine> --wait` wakes it on request; `lanq run --wait 300` wakes
  one sleeping machine by itself when no awake machine can take the job (`--no-wake` turns that off); `lanq health
  --wake` wakes every sleeper inside its hours. Wake-on-LAN must be enabled in the BIOS/UEFI and the network card
  driver and usually needs wired Ethernet: [WORKERS.md, Wake-on-LAN](WORKERS.md#wake-on-lan-for-a-sleeping-worker).
- **Sleep and updates.** `lanq makeready` reports what would break an overnight job (sleep timers, a pending reboot,
  update restarts) under the `overnight` verdict, with advice for the PC's owner.

## 10. Troubleshooting: the ten errors you are most likely to see

The messages below are what lanq prints (`<...>` is filled in). `lanq doctor --remote` finds most of them at once.

1. **`No config found. Pass --config FILE, set LANQ_CONFIG, or create <path> (a starter file: lanq init --config <path>). A machines file in the current directory is never picked up implicitly.`**
   lanq does not read `./machines.yml` by itself. Pass `--config ./machines.yml`, set `LANQ_CONFIG`, or move the file
   to the per-user path. `Config file not found: <path>. Check the path, or write a starter file with: lanq init --config <path>`
   means the path you gave does not exist.
2. **`<host> is not in your known_hosts yet, and lanq only connects to known hosts (StrictHostKeyChecking=yes). Run ssh <user@host> once in a terminal, check the fingerprint with the machine's owner, answer yes, then try again (ssh: Host key verification failed.)`**
   Do exactly that ([step 4](#4-connect-once-and-run-lanq-makeready-on-each-worker)). `lanq doctor` says the same as
   `no known_hosts entry for <host>: connect once with ssh <user@host>, check the fingerprint with the machine's owner, then accept it`.
   If instead it says **`the host key of <host> has CHANGED since it was saved in known_hosts`**, the PC was
   reinstalled or something is impersonating it: confirm with its owner before `ssh-keygen -R <host>`.
3. **`<user@host> refused your key. Check that your key is loaded (ssh-add -l; add it with ssh-add) or that key_env names it, and that its public key is in that user's .ssh/authorized_keys on the worker (for a Windows administrator account: C:\ProgramData\ssh\administrators_authorized_keys instead). Test with: ssh -o BatchMode=yes <user@host> true (ssh: <user@host>: Permission denied (publickey).)`**
   The key is not loaded, or the public key is in the wrong file or has loose permissions on the worker (see the
   [administrators trap](#windows-1011-worker); on Linux `.ssh` must be `700` and `authorized_keys` `600`).
4. **`Machine '<name>': key_env names the environment variable <VAR>, but <VAR> is not set. ...`** or
   **`Machine '<name>': key_env <VAR> points to <path>, which does not exist. ...`**
   Set the variable in the shell you run lanq from (PowerShell: `$env:LANQ_KEY = "$HOME/.ssh/lanq_ed25519"`), or
   remove `key_env` and use ssh-agent.
5. **`<host> refused the connection: its SSH server is not running, or a firewall rejects the ssh port ...`**
   Start sshd and make it automatic (Windows: `Start-Service sshd`, `Set-Service sshd -StartupType Automatic`; Linux:
   `sudo systemctl enable --now ssh`).
6. **`ssh: connect to host <host> port 22: Connection timed out`** (or `No route to host`). The PC is asleep, off,
   on another network, or a firewall drops port 22 (check the Windows rule in [step 3](#windows-1011-worker)). A PC
   with a `wake:` setting can be woken with `lanq wake <machine> --wait`. **`the host name <host> could not be resolved`**
   means the name in `ssh:` does not resolve; use the IP address.
7. **`Invalid config: ...`**, **`No machines configured.`** or a line starting **`Machine '<name>':`** (for example
   `a Windows work_root must be a drive path like D:/lanq (forward slashes, no spaces, no '..').`). The config is
   checked strictly when it loads, and an unknown key is an error too; the message names the key and the line.
8. **`[lanq] Rejected: Command '<cmd>' is not on policy.allowed_commands.`** (exit `2`), or `Directory '<dir>' is not
   on policy.allowed_directories.` Add the command (for example `dotnet test`) or folder to the policy.
9. **`[lanq] NoCapacity: No eligible machine. <machine>: <reason>; ...`** (exit `75`). Each machine says why:
   `lacks <capability>` (no machine has every `--needs` tag), `busy (slots 1/1)`, `low memory (0.8 GB free)`,
   `outside allowed hours`, `paused on the machine`, `window closing (5 min left)`, or its ssh error. Add `--wait 300`
   to wait for a free machine.
10. **`<tool>: '<tool> --version' failed in the job environment (...). If the tool is installed, a non-login ssh session does not see it: add its folder to path_prepend for this machine`**
    (from `lanq doctor --remote`), or **`git is not installed on the machine (transfer: git needs git; install it or set transfer: archive)`**
    (from `lanq health`). Add the tool's folder under `path_prepend:`, or install git, or set `transfer: archive`.

Still stuck: `lanq makeready <machine>` lists every WARN and FAIL with advice, and [WORKERS.md](WORKERS.md) has the
full worker checklist.
