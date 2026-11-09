# Changelog

## Unreleased (0.4.0)

Workers and windows: making old and shared PCs safe to lend to the queue.

- **Memory in health.** The probe reports available memory (Linux `MemAvailable` and free swap, plus PSI memory
  pressure `some avg10` where `/proc/pressure/memory` exists; Windows free physical memory) and `lanq health` shows
  `mem free X.X GB`. A machine with less than `policy.min_free_mem_gb` (default 1.0, per-machine `min_free_mem_gb`)
  is held ("low memory (x.x GB free)"), and on Linux one whose pressure is above `policy.max_mem_pressure` (default
  20) is held ("memory pressure"). A machine that does not report memory is not held for it.
- **`lanq run --mem <GB>`** sends a job only to a machine reporting at least that much available memory (never to one
  that cannot report it).
- **Per-machine `env:`** sets variables for the job process only, for example heap caps on small machines
  (`DOTNET_GCHeapHardLimitPercent`, `NODE_OPTIONS=--max-old-space-size=2048`, `MSBUILDNODECOUNT`,
  `CARGO_BUILD_JOBS`). Names must match `^[A-Za-z_][A-Za-z0-9_]*$`, values may not be empty (Windows PowerShell 5.1
  cannot set an empty variable, so both platforms refuse one) or contain control characters, and, as a best-effort
  guard, loader or shell hijack names (`LD_*`, `DYLD_*`, `GLIBC_TUNABLES`, `BASH_ENV`, `ENV`, `SHELLOPTS`, `BASHOPTS`,
  `PS4`, `PROMPT_COMMAND`, `PSModulePath`, `COMSPEC`, `PATHEXT`) are refused. Values are passed as quoted data in both
  shells.
- **Per-machine `path_prepend:`** puts absolute directories in front of `PATH` for the job, so tools installed by nvm,
  pyenv, rustup or sdkman are found in a non-login SSH session. `PATH` itself cannot be set through `env:`.
- **`priority: low|normal`** (default `low`): Linux jobs run under `nice -n 10` and `ionice -c3` when available;
  Windows jobs run at BelowNormal priority, inherited by their child processes. **This changes the default for
  existing configs**; set `priority: normal` on a dedicated build box.
- **PAUSE file.** If `<work_root>/PAUSE` exists on a worker, the machine is held ("paused on the machine"). Anyone at
  that PC can create or delete it. On Linux `PAUSE` or `pause` counts (those two spellings), on Windows any case. A job
  that is already running is not affected.
- **The worker enforces the job limit itself.** A job's own limit (`--timeout`, or the window end plus grace) plus one
  minute is enforced on the worker too, so a job outlives a vanished `lanq` by at most that minute: on Linux the job runs
  under `timeout -k 30 <seconds>` when `timeout` works there (exit `124`, children included); on Windows it runs in a
  child PowerShell that is killed with its whole tree (`taskkill /T /F`) when the wait runs out (exit `124`). The Linux
  job wrapper now runs `/bin/sh` by absolute path, so a `path_prepend:` folder cannot supply its own `sh`.
- **Availability windows v2.** `allowed_hours` still accepts one string (a bare `allowed_hours:` or `~` still means any
  time), and now also a list of entries
  `[Days ]HH:mm-HH:mm` with days `Mon`..`Sun`, ranges (`Mon-Fri`) and lists (`Sat,Sun`). A window that crosses
  midnight belongs to its start day. The start minute is inside a window and the end minute is not (0.3 also counted
  the end minute). Errors name the bad entry.
- **Windows use the worker's clock.** The probe reports the worker's local time; output from an older probe falls back
  to the clock of the machine running `lanq`, and `lanq health` then says `time: local`.
- **Daylight saving.** The probe also reports `now_epoch=` and `tz_offsets=`, the worker's UTC offset in minutes now
  and every hour for the next 36 hours (PowerShell `TimeZoneInfo.Local`; on Linux `date -d @EPOCH +%z`, which GNU and
  busybox date accept). The end of a window is turned into a real instant with that table, so a window over a
  clock change counts real time: a local end time that happens twice (clocks go back) is its earlier instant, and one
  that does not exist (clocks go forward) is the first instant after the jump. The table is hourly, so a change is
  placed to within an hour. A worker whose `date` cannot do this (or an older probe) gets wall-clock time, which can be
  an hour off on such a night.
- **Window close.** On a machine with windows a job may run until the window ends plus `policy.window_grace_minutes`
  (default 15) or until `--timeout`, whichever is sooner. If the window limit stops it, the job is cleaned up like a
  timeout, exits `124`, and the status line is `[lanq] WindowClosed`. A machine whose window ends in less than
  `policy.min_window_minutes` (default 10) takes no new job ("window closing"). The same check runs again after upload
  and prepare: if what is left of the window plus grace is then below `policy.min_window_minutes`, the job is not
  started, the machine is cleaned up, and the result is `NoCapacity` (exit `75`, "window closing").

Transfer and supply chain:

- **One cache per repository on git workers.** The single shared `<work_root>/repo.git` is replaced by
  `<work_root>/repos/<repoid>.git`, where `<repoid>` is the first 16 hex digits of the repository's root commit (the
  ordinally smallest one if the history has several roots). Different projects no longer share one object store. An
  existing `repo.git` is left alone and no longer used; delete it whenever convenient.
- **Hardened git on workers.** Every git call in the worker scripts (init, fetch, worktree add/remove/prune,
  update-ref) runs with `-c core.hooksPath=<null device> -c core.fsmonitor=false -c protocol.allow=never
  -c protocol.file.allow=never` (`/dev/null` on Linux, `NUL` on Windows); only the bundle fetch re-allows reading
  that one local file. A hook planted in a worker cache, or a hooks path in the worker's git config, no longer runs.
  Every git call also runs with `-c core.attributesFile=<null device>`, `GIT_CONFIG_NOSYSTEM=1` and `GIT_CONFIG_GLOBAL`
  at the null device (git 2.32 or later; `lanq doctor --remote` warns "git older than 2.32: global config not
  isolated" for an older worker git, which still reads the global config). Before each fetch, prepare deletes the
  cache's `info/attributes` and checks the cache's own `config`: any key besides the ones `git init --bare` writes
  (`core.repositoryformatversion`, `filemode`, `bare`, `symlinks`, `ignorecase`, `precomposeunicode`,
  `logallrefupdates`, `extensions.objectformat`, `refstorage`), for example `filter.*`, `include.*`, `includeIf.*`,
  `alias.*`, `remote.*`, `credential.*` or `url.*`, makes it delete and recreate that cache (one line in the job output;
  that job sends a full bundle). Tests plant a smudge filter, an `include.path` and a global config filter and check
  that none of them runs.
- **Incremental bundles.** `lanq` records, per machine and repository, the last commit a worker has fetched
  (`<state>/bases.json`, written atomically, updated only after a successful fetch), and the worker cache keeps
  `refs/lanq/base` on the last fetched snapshot. The next job sends `git bundle create … --not <base>` when that commit
  is still a local ancestor of the snapshot, so usually only the new commits and the uncommitted overlay travel. The
  worker checks the bundle with `git bundle verify` before fetching; if its cache lost the base, the job falls back
  once to a full bundle automatically (one log line says so). Rewritten history, a pruned base or a missing or
  unreadable `bases.json` simply mean a full bundle. A `bases.json` that cannot be read or written (for example locked by a
  parallel `lanq` on Windows) is retried twice, then one warning line is printed and the job goes on. Bundles are now built when a git machine actually gets the job.
  Archive mode is unchanged.
- **Secret scan of committed file names.** Besides the uncommitted overlay, the names of every committed file in the
  HEAD tree are checked against `.env`, `.env.*`, `id_rsa`, `id_ed25519`, `*.pem`, `*.pfx`, `*.p12`, `*.key`,
  `credentials.json` and `*.kdbx`, because a bundle or archive would carry them. A match refuses the job before
  anything is sent. `.npmrc` and `.pypirc` files, committed or uncommitted, are not refused by name (registry-only ones are common):
  their content (up to 64 KB) is checked, and only a literal credential refuses the job (`.npmrc`: a non-empty
  `_authToken`, `_auth`, `_password` or `password`, including `//host/:_authToken=`, unless it is just an environment
  reference such as `${NPM_TOKEN}`; `.pypirc`: a non-empty `password`). The refusal names the file and key, never
  the value. Obvious samples pass: names ending in `.example` or `.sample`, names containing
  `.example.`, and anything under a `fixtures/` or `testdata/` folder. **Behaviour change:** a repository that commits
  such a file (for example a test certificate outside those folders, or an `.npmrc` with a literal token) is now refused until the file is
  renamed or moved. A long list of findings is cut to the first ten.
- **Stale slots are reclaimed.** A slot file now records its own deadline (worker clock: now + the job's `--timeout`
  + 40 minutes for transfer and cleanup). When a job looks for a slot, a slot more than 30 minutes past its deadline
  (left by a crashed or killed `lanq`) is reclaimed: under an exclusive lock file `slot-N.reclaim` (one older than ten
  minutes, left by a reclaimer that died, is cleared) the slot is read again, and only if it is still that stale file
  is it renamed away to a unique name, deleted and created afresh like a free slot (if another job wins it, the next
  slot is tried; nothing is ever put back, and no hard links are needed). If the stale slot's job is somehow still
  running (its `locks/job-<id>.pid` names a live process: on Linux one whose working folder is in that job's folder,
  on Windows a PowerShell process), its process group or tree is killed first. A live slot, or one without a valid
  deadline (written by an older lanq, or not 1 to 18 decimal digits; `08` is read as 8), is never touched.
- **Supply chain.** Every project commits a `packages.lock.json` and CI restores in locked mode
  (`RestoreLockedMode` when `CI=true`); Dependabot proposes grouped weekly NuGet and GitHub Actions updates; the
  release workflow attests the `.nupkg`'s build provenance (`actions/attest-build-provenance`, pinned by SHA, with
  `id-token`/`attestations` write granted to that one job). README: "Verify a release" with `gh attestation verify`.

Command line:

- **`lanq makeready <machine>`** and **`lanq makeready --ssh user@host --os windows|linux`** (for a PC not in
  `machines.yml` yet) run the read-only [lanq-makeready](https://github.com/jangolath/lanq-makeready) v0.1.0 check on a
  worker. The two scripts are built in, byte-identical to that release (a test pins their SHA-256 from its
  `SHA256SUMS`; `scripts/sync-makeready.sh <tag>` updates them only after `sha256sum -c` and `gh attestation verify`),
  sent on ssh stdin with `-Redact`/`--redact` and run from memory: nothing is written to the worker. The JSON report
  is treated as untrusted (1 MB cap, source-generated parsing, schema `lanq-makeready/v1`, every string sanitised,
  nothing executed). Output: verdicts, each WARN/FAIL check with its advice, the capability diff against
  `machines.yml`, a suggested entry, and "lanq never changes a worker". `--json` prints the validated report. Exit `0`
  ready, `1` not ready, `2` bad arguments, `70` ssh or unusable report, `75` the machine is paused or outside its
  allowed hours (`--force` scans anyway).
- **`lanq doctor`** checks your own setup with no network: ssh/scp/git/ssh-keygen on PATH, the state folder, the
  config, and per machine a `known_hosts` entry (resolved with `ssh -G`, `[host]:port` for other ports) and the
  `key_env` file and passphrase. **`lanq doctor --remote`** adds one ssh call per machine (four at a time): `work_root`
  writable, git or tar, and each capability's tool (`dotnet`, `node`, `npm`, `python`, `go`, `rust`, `java`, `docker`)
  run in the job environment, so a tool missing from a non-login SSH session (fix: `path_prepend:`) shows up before a
  job fails. Exit `0` when every line is OK.
- **`--json`** for `lanq health` (one array: `name`, `state`, `reasons`, `load`, `diskFreeGb`, `memFreeGb`,
  `slotsUsed`, `maxJobs`, `localTime`) and `lanq run` (the job's output goes to stderr, then one object on stdout:
  `status`, `exitCode`, `machine`, `jobId`, `durationSeconds`, `message`). Agents should prefer it.
- **`lanq unlock <machine>`** removes slot files past their recorded deadline at once (the same locked reclaim, which
  also stops such a slot's job if it is somehow still running) and says what it removed and kept; `--all --yes` removes every slot file (also pre-0.4 ones without a deadline), after a warning that a running
  job there would lose its slot. `--all` without `--yes` is refused.
- Options are checked per command: an option the command does not use (for example `lanq health --needs x`) is now an
  error (exit `2`) instead of being ignored.
- Fixes: an uncommitted `.npmrc` / `.pypirc` gets the same content check as a committed one (it was refused by
  name); the health probe no longer counts the transient `slot-N.stale.<pid>` file of a slot reclaim as a used slot.

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
