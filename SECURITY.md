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
them. Output from a worker is treated as untrusted: control and invisible formatting characters (C0 except tab, DEL,
C1, bidi controls and marks, zero-width characters, U+2028/U+2029 and U+2060 to U+2064) are shown as `?`, and so are
they in error messages that quote your own input.

Per-machine `env:` values reach the job as data (one single-quoted word for `env` on Linux, a single-quoted
PowerShell string on Windows) and never as shell code; names are restricted to `[A-Za-z_][A-Za-z0-9_]*`, values may
not be empty or contain control characters, and names that hijack the program loader or a shell (`LD_*`, `DYLD_*`,
`GLIBC_TUNABLES`, `BASH_ENV`, `ENV`, `SHELLOPTS`, `BASHOPTS`, `PS4`, `PROMPT_COMMAND`, `PSModulePath`, `COMSPEC`,
`PATHEXT`) are refused. That list is best effort, a guard against mistakes rather than a complete one. `path_prepend` entries must be absolute. Both are written by you in your own config: they are a
convenience for your machines, not a boundary against someone who can edit that file.

In git mode a worker receives the repository's history, not just the current files: the first job for a repository
on a machine sends the full history of HEAD, later jobs only the new commits. The secret scan covers the command
line, the uncommitted changes (content and names), the names of committed files in HEAD (`.env`, `id_rsa`,
`.git-credentials`, `.netrc`, `terraform.tfstate`, `*.pem` and similar, from the same list as uncommitted files, with
samples under `fixtures/` or `testdata/`, or named `*.example` / `*.sample`, allowed), and the content of `.npmrc` /
`.pypirc` / `.yarnrc.yml` files, committed or uncommitted (refused only for a literal token or password; `${NPM_TOKEN}`-style
references pass, and the value is never printed). It does not read other committed file contents or older commits, so a secret committed and later deleted still travels in a
bundle. Archive mode sends only the current files, never history.

`lanq makeready` runs the embedded lanq-makeready scripts, which are byte-identical to a GitHub release whose SHA-256
values a test pins; they are updated only through `scripts/sync-makeready.sh`, which checks the release's
`SHA256SUMS` and its build-provenance attestation first. The script is sent on ssh stdin and runs from memory, so
nothing is written to the worker. Its JSON report is untrusted: read up to 1 MB, parsed by a source-generated
deserializer (no reflection), validated against the `lanq-makeready/v1` schema, sanitised before display, and never
executed. `lanq doctor --remote` writes and removes one empty temporary file in `work_root` and runs version
commands (`go` with `GOTOOLCHAIN=local`, `cargo` with `RUSTUP_AUTO_INSTALL=0`, each stopped after 20 seconds) in the job
environment, and, like `makeready`, skips a machine that is paused or outside its allowed hours unless `--force`; `lanq doctor` without `--remote` makes no network
connection. When a capability's tool is missing there, doctor also lists a few install folders and asks the dotnet on `PATH`
for its SDKs and runtimes (read-only, about five seconds) to suggest a `path_prepend:` line; it never changes the config or the worker.

On a Windows worker lanq writes one helper script to `work_root/bin/lanq-<hash>.ps1` before each job (if it is not there
yet); the job wrapper, stop, cleanup, slot reclaim and doctor scripts dot-source it to run the job in a Windows job
object and to stop process trees without WMI. Like everything in `work_root`, it is only as safe as the build account:
a job runs as that account and could replace the file, and so change what lanq's later scripts on that worker do. This
adds no power a job does not already have there (it can already leave programs running or change that account's
files), but it is one more reason to give the build account nothing but its work folder.

Wake-on-LAN is opt-in per machine (`wake:` in the config). The magic packet is a plain, unauthenticated UDP broadcast
that only works inside the LAN: anyone on the LAN who knows a machine's MAC address can send the same packet, so
treat it as a convenience that does nothing but power a PC on, not as access control. `broadcast` must be an IPv4
literal (no host names, no IPv6), the MAC must be a strict `aa:bb:cc:dd:ee:ff` / `aa-bb-cc-dd-ee-ff` value, the port is
7 or 9, and config loading refuses a broadcast address that is not 255.255.255.255 or private or link-local (no public,
multicast, loopback or 0.0.0.0 address). `lanq` never wakes a machine on its own outside its `allowed_hours` (judged on
this computer's clock), at most once per machine per run, never from `lanq health` unless you pass `--wake`, and from
`lanq run` only as a last resort when no awake machine can take the job and `--wait` is above 0; only
`lanq wake <machine>` ignores the hours, because you named the machine.

On a git worker every git call runs with hooks disabled (`core.hooksPath` at the null device), `core.fsmonitor` off,
`core.attributesFile` at the null device, all transports refused except reading the uploaded bundle, and with
`GIT_CONFIG_NOSYSTEM=1` and `GIT_CONFIG_GLOBAL` at the null device, so the system and the ssh user's global git config
(git-lfs filters, includes, aliases) are not read. `GIT_CONFIG_GLOBAL` needs git 2.32 or later; with an older git the
global config is still read, and `lanq doctor --remote` warns. Before each fetch the per-repository cache's own `config`
is checked: anything but the keys `git init --bare` writes (`core.repositoryformatversion`, `core.filemode`,
`core.bare`, `core.symlinks`, `core.ignorecase`, `core.precomposeunicode`, `core.logallrefupdates`,
`extensions.objectformat`, `extensions.refstorage`), or any top-level entry besides the ones git itself creates there
(`HEAD`, `config`, `description`, `hooks`, `info`, `objects`, `refs`, `reftable`, `branches`, `packed-refs`, `logs`,
`worktrees`, `FETCH_HEAD`, `ORIG_HEAD`, `shallow`, gc and lock files), in particular a `commondir` or `gitdir` file that would make git read
another directory's config and attributes, makes lanq delete and recreate that cache, and the cache's
`info/attributes` is deleted. This check and the git calls that follow are not atomic: another process running as the
same worker user could change the cache in between. That user is already trusted (it runs the jobs), so this is a
guard against a planted cache, not against a concurrent attacker with the same rights. The repository's own committed `.gitattributes` still apply, but they can only name
filter drivers, which are defined in config that lanq no longer reads.

`lanq run --fetch` treats the worker as untrusted: a family PC can be compromised, and a job can create any file it
likes in its folder. Fetched bytes are data only: lanq never executes them, never extracts them with an archive tool
and never lets them choose where they land. The worker script lists only regular files below the job's working folder
whose real path stays inside the job folder, never follows symlinks, junctions or other reparse points, skips
hard-linked files (Linux; Windows hard links are not detected) and devices or FIFOs, and sends each file as a header
line, base64 lines and its SHA-256. lanq does not trust any of that: it re-validates every path (relative, no `..`,
no backslash or `:`, no control or invisible characters, no Windows device names (also with spaces before the
extension, such as `NUL .txt`), trailing dots or spaces, length limits, no case-insensitive collisions, and it must
match a requested pattern, checked with a non-backtracking matcher so a crafted name cannot make it hang), enforces
the count and size caps on the bytes actually received, requires the exact announced size and a matching hash,
streams each file to disk as a short `.lanq-partial-N` file in the same folder (created with `CreateNew`, deleted on any failure or on Ctrl+C) and
renames it to its name only when complete, never over an existing file. Only the worker's stdout is read as frames;
its stderr is kept apart as a bounded, sanitised diagnostic, so a stray warning cannot corrupt a file. Every folder
from the repository top (or the `--fetch-dir` you named) down to `<out>/<job id>/` is checked and created one level
at a time, and a symlink, junction or other reparse point anywhere on that path (for example a `.lanq` link committed
in a repository) refuses the fetch; `.lanq/.gitignore` is never written through such a link. Names are sanitised before
they are printed. Before fetching from a job that timed out or was stopped by its machine's hours, lanq stops it with
the same recorded-process kill as cleanup, so the files are no longer being written. Two limits remain. First, the
worker-side checks and the reads are not atomic: a process the job left running outside its recorded process group
could swap a checked file for a link to another file the build user can read, and that file's bytes would come back (still only
as data, under its checked name, within the caps); a compromised worker can send any bytes it likes anyway, so treat
fetched files like any download from that PC. Second, lanq checks the folders of the output path itself but a local
process with your rights could still race it; that is outside this threat model.

A job's processes are identified before lanq signals them (cleanup, the stop before a fetch, stale-slot reclaim). On
Windows a recorded process is killed only when its pid and start time both match. On Linux the job wrapper records the
process group, the group leader's start time and the boot id, and every job process carries `LANQ_JOB=<job id>` in its
environment. Nothing is signalled when the boot id differs (a pid file that survived a reboot); otherwise the group is
signalled when its leader still has the recorded start time, or when the leader is gone and at least one live member of
the group carries this job's `LANQ_JOB`, so a group number reused by another program is left alone. A job that clears
its own environment and outlives its leader is not found this way. Without /proc (macOS, BSD) the older checks apply:
the leader's working folder inside the job folder, or `ps` showing it still leads the recorded group.

Slot files on a worker are coordinated with per-slot lock files that each hold their creator's unique token; a
lanq process removes a lock only after renaming it away and finding its own token in it. On Linux this is done with
`mv`, `cat` and `set -C`, which give no atomic compare-and-delete: if a reclaimer that held a lock for more than ten
minutes is overtaken by two others at the same moment, two of them can briefly both believe they hold it. The worst
outcome is that one slot is reclaimed twice, so one extra job may start beside another; no file outside the
`work_root` locks folder is affected.

## Supply chain and release artifacts

- Every GitHub Action is pinned to a full commit SHA, and the Linux build containers (Microsoft's .NET 10 cross-build
  images) and the Ubuntu 18.04 smoke-test image are pinned by digest. Every project commits a `packages.lock.json`
  (the native build has its own `packages.native.lock.json`), and CI restores in locked mode. New package versions are
  pinned exactly.
- Each release attaches one `SHA256SUMS` covering every asset and a build-provenance attestation for each asset
  (binaries, `.nupkg`, the Scoop `lanq.json`, `SHA256SUMS`), signed with the release workflow's OIDC identity through
  Sigstore's public-good instance. Only the `attest` job can mint that token; it runs no project code. Verify with
  `gh attestation verify <file> --repo jangolath/lanqueue`; a hash alone proves integrity only if you got it from a
  channel you trust. An attestation shows where and how a file was built, not that it is free of bugs.
- Nothing is published unless every native binary built and passed its smoke test; the release is created as a draft,
  the package pushed to nuget.org (behind the `nuget-release` environment), and only then is the release made public.
  A re-run reuses a draft only if it holds the same `SHA256SUMS`. Tags must be strict semver and reach the jobs only
  through `env:`; a pre-release tag becomes a GitHub prerelease that is never marked latest. Checkouts do not keep the
  workflow token.
- The pinned .NET runtime cannot fall behind silently: CI and the release fail when the SDK carries a newer 10.0
  patch, and a weekly workflow fails when Microsoft's `releases.json` lists one.
- The Scoop manifest pins exact versioned URLs and SHA-256 hashes, and Scoop refuses a zip whose hash differs.
  Installing from the `releases/latest` URL means each `scoop update` trusts whatever manifest the latest release
  carries, which is the same trust as downloading the latest release by hand.
- The native binaries are self-contained: they include the .NET runtime and libraries of the patch they were built
  with (pinned as `LanqRuntimePackVersion`). A .NET security fix therefore reaches them only through a new `lanq`
  release; the NuGet tool uses the runtime on your machine and gets .NET fixes from your .NET updates. The macOS binary
  is ad-hoc signed, not notarized; the Windows binaries are not Authenticode-signed.

It does not:
- sandbox the allowlisted program (it runs with the remote user's rights; use a low-privilege account);
- guarantee that no secret leaves your machine (the scan is pattern-based);
- protect against a compromised remote host or a compromised local machine.

Supported version: the latest release.
