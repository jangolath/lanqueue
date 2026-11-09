#!/bin/sh
# lanq-makeready: READ-ONLY check of whether this Linux/macOS machine can be a LanQueue worker.
# Changes nothing, installs nothing, makes no network calls, needs no root.
# Every fix is printed as advice for you to run yourself (or not).
#
# Usage: lanq-makeready.sh [--json PATH|-] [--redact] [--hours HH:MM-HH:MM] [--quiet]
#        --json - writes ONLY the JSON to stdout (for running over SSH: sh -s -- --json - --redact < this-file)
# Exit:  0 = worker-ready, 1 = not ready, 2 = bad arguments.

TOOL_VERSION=0.1.0
SEP=$(printf '\037')
CHECKS=""

# BEGIN ADVICE
# The ONE table of advice text. Nothing here is ever executed by this script.
advice() {
  case "$1" in
    ssh.install) echo "Install and start an SSH server yourself, for example (Debian/Ubuntu): sudo apt install openssh-server ; sudo systemctl enable --now ssh" ;;
    ssh.start) echo "sshd is installed but not running. Start it yourself: sudo systemctl enable --now ssh   (the unit is called sshd on Fedora/RHEL)" ;;
    ssh.nokeys) echo "Put the orchestrator public key in the build user's ~/.ssh/authorized_keys (chmod 600). Fine to ignore if another user is the build user." ;;
    ssh.mac) echo "On macOS check System Settings > General > Sharing > Remote Login yourself. Note: LanQueue v0.3 workers are windows|linux only." ;;
    git.install) echo "git is only needed for transfer: git. Without it use transfer: archive, or install git with your package manager." ;;
    tar.busybox) echo "busybox tar (Alpine, minimal images) is refused by LanQueue for transfer: archive. Install GNU tar or bsdtar, for example on Alpine: apk add tar" ;;
    tar.missing) echo "transfer: archive needs GNU tar or bsdtar, plus gzip. Install them with your package manager." ;;
    docker.remote) echo "A remote docker endpoint (DOCKER_HOST or a non-local context) is never probed." ;;
    os.mac) echo "LanQueue v0.3 workers are windows|linux only, so this Mac cannot be a worker yet." ;;
    arch32) echo "32-bit x86 Linux is not supported by .NET 10. Install a 64-bit OS (the CPU may support it)." ;;
    ram.low) echo "Less than 4 GB RAM: builds will swap and be slow. Use max_jobs: 1; below 2 GB .NET SDK builds are unreliable." ;;
    disk.hdd) echo "A spinning disk makes builds slow. An SSD (even a small cheap one) is the single best upgrade for an old PC." ;;
    disk.low) echo "Free disk space is low. Each job needs room for a worktree plus build output; keep at least 20 GB free, below 10 GB the worker is refused." ;;
    dotnet.glibc) echo "The .NET 10 SDK needs glibc 2.27 or newer (musl 1.2.3 or newer on Alpine). Use a newer distribution release (Ubuntu 22.04/24.04, Debian 13, Alpine 3.21+)." ;;
    dotnet.mac) echo ".NET 10 supports macOS 15 and newer." ;;
    docker.daemon) echo "The docker client is installed but the engine is not answering (or you lack permission). Start the service or check group membership yourself." ;;
    docker.ram) echo "Under 4 GB RAM: containers are not recommended on this machine." ;;
    power.sleep) echo "The machine may suspend while idle. On a desktop review it yourself in power settings; on systemd you can mask sleep targets: sudo systemctl mask sleep.target suspend.target hibernate.target" ;;
    reboot.pending) echo "A restart is pending; the machine may reboot during a job. Restart it at a time you choose." ;;
    startup.many) echo "Many enabled user services slow login and use RAM. Review them yourself with systemctl --user list-unit-files. This tool never disables anything." ;;
    temp.big) echo "Large cache folders. Review before deleting; this tool never deletes anything." ;;
    *) echo "" ;;
  esac
}
# END ADVICE

# ---------- pure helpers (no I/O): used by tests with fixtures ----------

add_check() { # id title status detail [advice-key] [needs-admin]
  _adv=""
  [ -n "$5" ] && _adv=$(advice "$5")
  CHECKS="${CHECKS}$1${SEP}$2${SEP}$3${SEP}$4${SEP}${_adv}${SEP}${6:-false}
"
}

st() { # status of a check id, SKIP when absent
  _s=$(printf '%s' "$CHECKS" | while IFS="$SEP" read -r _i _ _st _; do
    if [ "$_i" = "$1" ]; then echo "$_st"; fi
  done)
  echo "${_s:-SKIP}"
}

worst() {
  _w=SKIP
  for _x in "$@"; do
    case "$_x" in
      FAIL) _w=FAIL ;;
      WARN) [ "$_w" != FAIL ] && _w=WARN ;;
      OK) [ "$_w" = SKIP ] && _w=OK ;;
      *) ;;
    esac
  done
  echo "$_w"
}

ver_num() { # "2.35-0ubuntu3" -> 2035000
  _v=$1
  _a=${_v%%.*}
  _r=${_v#*.}; [ "$_r" = "$_v" ] && _r=0
  _b=${_r%%.*}
  _c=${_r#*.}; [ "$_c" = "$_r" ] && _c=0
  _a=${_a%%[!0-9]*}; _b=${_b%%[!0-9]*}; _c=${_c%%[!0-9]*}
  echo $(( ${_a:-0} * 1000000 + ${_b:-0} * 1000 + ${_c:-0} ))
}

ver_ge() { [ "$(ver_num "$1")" -ge "$(ver_num "$2")" ]; }

gb() { awk -v m="$1" 'BEGIN { printf "%.1f", m / 1024 }'; }

valid_hours() { # HH:MM-HH:MM, hours 00-23, start differs from end
  case "$1" in
    [0-2][0-9]:[0-5][0-9]-[0-2][0-9]:[0-5][0-9]) ;;
    *) return 1 ;;
  esac
  _s=${1%-*}; _e=${1#*-}
  [ "$_s" = "$_e" ] && return 1
  [ "${_s%%:*}" -le 23 ] && [ "${_e%%:*}" -le 23 ]
}

ram_installed_mb() { echo $(( ($1 + 1023) / 1024 * 1024 )); } # MemTotal is below installed RAM (kernel/firmware reserve): round up to whole GiB

safe_name() { # lowercase [a-z0-9-], no leading/trailing '-', fallback old-pc
  _n=$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9-' '-' | sed -e 's/^-*//' -e 's/-*$//' | cut -c1-63)
  echo "${_n:-old-pc}"
}

valid_ssh_user() { # conservative: letters, digits, dot, underscore, hyphen; not starting with '-'
  case "$1" in
    ""|-*|*[!A-Za-z0-9._-]*) return 1 ;;
    *) return 0 ;;
  esac
}

sdk_ge8() { # true when a space-separated list of SDK majors has one >= 8
  for _m in $1; do
    case "$_m" in *[!0-9]*|"") continue ;; esac
    [ "$_m" -ge 8 ] && return 0
  done
  return 1
}

docker_remote() { # $1 = DOCKER_HOST, $2 = context name; true when the engine is remote
  case "$1" in ""|unix://*|npipe://*) ;; *) return 0 ;; esac
  case "$2" in ""|default|desktop-linux|desktop-windows|orbstack|colima|rancher-desktop) return 1 ;; *) return 0 ;; esac
}

have_session_bus() { [ -n "${DBUS_SESSION_BUS_ADDRESS:-}" ]; }

# Redaction works on single VALUES before anything is rendered; finished text/JSON is never rewritten.
rv() {
  _t=$1
  for _tok in "$R_HOME" "$R_USER" "$R_HOST"; do
    [ "${#_tok}" -ge 2 ] && [ "$_tok" != root ] || continue
    _e=$(printf '%s' "$_tok" | sed -e 's/[][\.*^$/|?+(){}]/\\&/g')
    _t=$(printf '%s\n' "$_t" | sed -E -e "s/(^|[^A-Za-z0-9_])$_e([^A-Za-z0-9_]|\$)/\\1<redacted>\\2/g" -e "s/(^|[^A-Za-z0-9_])$_e([^A-Za-z0-9_]|\$)/\\1<redacted>\\2/g")
  done
  printf '%s' "$_t"
}

redact_facts() { # only free-text facts; fields that drive logic are untouched
  R_HOME=$HOME; R_USER=$F_USER; R_HOST=$F_HOST
  F_OSNAME=$(rv "$F_OSNAME"); F_CPU=$(rv "$F_CPU"); F_GIT=$(rv "$F_GIT"); F_DOCKER_SERVER=$(rv "$F_DOCKER_SERVER"); F_TEMP=$(rv "$F_TEMP")
  for _n in node python java go rustc; do
    eval "_v=\${F_T_$_n:-}"
    [ -n "$_v" ] || continue
    _rv=$(rv "$_v")
    eval "F_T_$_n=\$_rv"
  done
  F_HOST="<redacted>"; F_USER="<redacted>"
}

classify_tar() { # text of tar --version (stderr included) -> gnu | bsd | busybox | none
  case "$1" in
    *"GNU tar"*) echo gnu ;;
    *bsdtar*) echo bsd ;;
    *[Bb]usy[Bb]ox*) echo busybox ;;
    *) echo none ;;
  esac
}

eval_checks() { # reads F_* facts, fills CHECKS
  CHECKS=""
  _os="${F_OSNAME:-$F_OS}"
  if [ "$F_OS" = Darwin ]; then add_check os.version OS FAIL "$_os (macOS): LanQueue workers are windows|linux only" os.mac
  else add_check os.version OS OK "$_os"; fi
  case "$F_ARCH" in
    i?86|x86) add_check os.arch Architecture FAIL "32-bit ($F_ARCH)" arch32 ;;
    *) add_check os.arch Architecture OK "$F_ARCH" ;;
  esac
  add_check cpu CPU OK "${F_CPU:-unknown}, ${F_CORES:-?} logical CPUs, AVX2: ${F_AVX2:-n/a} (informational)"
  _rs=OK; _ra=""
  if [ "${F_RAMMB:-0}" -lt 2048 ]; then _rs=FAIL; _ra=ram.low
  elif [ "${F_RAMMB:-0}" -lt 4096 ]; then _rs=WARN; _ra=ram.low; fi
  add_check ram Memory "$_rs" "$(gb "${F_RAMMB:-0}") GB installed (approx.), $(gb "${F_FREERAMMB:-0}") GB available" "$_ra"
  _ds=OK; _da=""
  _min=${F_SYSFREEGB:-0}; [ "${F_WORKFREEGB:-0}" -lt "$_min" ] && _min=${F_WORKFREEGB:-0}
  if [ "$_min" -lt 10 ]; then _ds=FAIL; _da=disk.low
  elif [ "$F_DISK" = HDD ]; then _ds=WARN; _da=disk.hdd
  elif [ "$_min" -lt 20 ]; then _ds=WARN; _da=disk.low; fi
  add_check disk Disk "$_ds" "system drive $F_SYSFREEGB GB free, work drive $F_WORKFREEGB GB free, disk type: ${F_DISK:-Unknown}" "$_da"
  case "$F_SSHD" in
    running) add_check ssh.server "SSH server" OK "sshd running" ;;
    socket) add_check ssh.server "SSH server" OK "sshd is socket-activated (starts on the first connection)" ;;
    stopped) add_check ssh.server "SSH server" FAIL "sshd installed but not running" ssh.start ;;
    notinstalled) add_check ssh.server "SSH server" FAIL "no sshd found" ssh.install ;;
    *) add_check ssh.server "SSH server" SKIP "could not determine without admin rights" ssh.mac true ;;
  esac
  if [ "$F_AUTHKEYS" = yes ]; then add_check ssh.keys "SSH keys" OK "authorized_keys present and non-empty"
  else add_check ssh.keys "SSH keys" WARN "no authorized_keys for this user" ssh.nokeys; fi
  if [ -n "$F_GIT" ]; then add_check git git OK "$F_GIT"
  else add_check git git WARN "not found (needed for transfer: git only)" git.install; fi
  case "$F_TARKIND:$F_GZIP" in
    gnu:yes|bsd:yes) add_check tar tar OK "$F_TARKIND tar and gzip found" ;;
    busybox:*) add_check tar tar FAIL "busybox tar: refused by LanQueue for transfer: archive" tar.busybox ;;
    gnu:*|bsd:*) add_check tar tar FAIL "gzip not found" tar.missing ;;
    *) add_check tar tar WARN "tar not found (needed for transfer: archive only)" tar.missing ;;
  esac
  eval_dotnet
  eval_docker
  for _t in node python java go rustc; do
    eval "_v=\${F_T_$_t:-}"
    if [ -n "$_v" ]; then add_check "$_t" "$_t" OK "$_v"
    else add_check "$_t" "$_t" SKIP "not installed (optional, only if your jobs use it)"; fi
  done
  case "$F_SLEEP" in
    never|masked) add_check power.sleep "Sleep/suspend" OK "will not sleep ($F_SLEEP)" ;;
    sleeps) add_check power.sleep "Sleep/suspend" WARN "may sleep: ${F_SLEEP_DETAIL:-idle suspend enabled}" power.sleep ;;
    *) add_check power.sleep "Sleep/suspend" SKIP "could not determine (no pmset, gsettings or systemd)" ;;
  esac
  if [ "$F_REBOOT" = yes ]; then add_check reboot.pending "Pending reboot" WARN "a restart is pending" reboot.pending
  else add_check reboot.pending "Pending reboot" OK "none"; fi
  add_check wol "Wake-on-LAN" SKIP "needs root to read (ethtool)" "" true
  if [ -z "$F_STARTUP" ]; then add_check startup "Startup items" SKIP "could not read"
  elif [ "$F_STARTUP" -gt 20 ]; then add_check startup "Startup items" WARN "$F_STARTUP enabled user services/agents" startup.many
  else add_check startup "Startup items" OK "$F_STARTUP enabled user services/agents"; fi
  if [ -z "$F_TEMP" ]; then add_check temp "Cache folders" SKIP "none readable"
  elif [ "${F_TEMP_BIGMB:-0}" -ge 5120 ]; then add_check temp "Cache folders" WARN "$F_TEMP" temp.big
  else add_check temp "Cache folders" OK "$F_TEMP"; fi
}

eval_dotnet() {
  _t=".NET 10 SDK"
  _have="no SDK found"; [ -n "$F_DOTNET" ] && _have="SDK majors found: $F_DOTNET"
  if [ "$F_OS" = Darwin ]; then
    if [ "${F_OSVER:-0}" -ge 15 ]; then add_check dotnet "$_t" OK "macOS $F_OSVER ($_have)"
    else add_check dotnet "$_t" WARN "macOS ${F_OSVER:-unknown} is older than 15 ($_have)" dotnet.mac; fi
    return
  fi
  case "$F_ARCH" in
    i?86|x86) add_check dotnet "$_t" FAIL "32-bit x86 Linux is not supported ($_have)" arch32; return ;;
  esac
  if [ "${F_RAMMB:-0}" -lt 2048 ]; then add_check dotnet "$_t" FAIL "under 2 GB RAM ($_have)" ram.low; return; fi
  case "$F_LIBC" in
    glibc)
      _min=2.27; case "$F_ARCH" in arm*) _min=2.35 ;; esac
      if ! ver_ge "${F_LIBCVER:-0}" "$_min"; then add_check dotnet "$_t" FAIL "glibc $F_LIBCVER is older than $_min ($_have)" dotnet.glibc; return; fi
      _lib="glibc $F_LIBCVER" ;;
    musl)
      if ! ver_ge "${F_LIBCVER:-0}" 1.2.3; then add_check dotnet "$_t" FAIL "musl $F_LIBCVER is older than 1.2.3 ($_have)" dotnet.glibc; return; fi
      _lib="musl $F_LIBCVER" ;;
    *) add_check dotnet "$_t" WARN "C library not recognised ($_have)" dotnet.glibc; return ;;
  esac
  if [ "${F_RAMMB:-0}" -lt 4096 ]; then add_check dotnet "$_t" WARN "$_lib ok but low RAM ($_have)" ram.low
  else add_check dotnet "$_t" OK "$_lib ($_have)"; fi
}

eval_docker() {
  if [ "$F_DOCKER_CLI" = yes ] && [ "$F_DOCKER_REMOTE" = yes ]; then add_check docker Docker SKIP "remote docker endpoint not probed" docker.remote; return; fi
  if [ -n "$F_DOCKER_SERVER" ]; then add_check docker Docker OK "engine answering (server $F_DOCKER_SERVER)"; return; fi
  if [ "$F_DOCKER_CLI" = yes ]; then add_check docker Docker WARN "client installed but engine not reachable" docker.daemon; return; fi
  case "$F_ARCH" in
    i?86|x86) add_check docker Docker FAIL "32-bit x86 cannot run Docker" arch32; return ;;
  esac
  if [ "${F_RAMMB:-0}" -lt 4096 ]; then add_check docker Docker WARN "only $(gb "${F_RAMMB:-0}") GB RAM: containers not recommended" docker.ram
  elif [ "$F_OS" = Darwin ]; then add_check docker Docker OK "not installed; Docker Desktop could run (4 GB RAM minimum met)"
  else add_check docker Docker OK "not installed; Linux runs Docker Engine natively (cgroup ${F_CGROUP:-unknown})"; fi
}

eval_verdicts() { # sets V_* from CHECKS and F_*
  _w=$(worst "$(st ssh.server)" "$(st ssh.keys)" "$(st disk)" "$(st os.version)")
  _g=FAIL; [ -n "$F_GIT" ] && _g=OK
  _a=FAIL; case "$F_TARKIND:$F_GZIP" in gnu:yes|bsd:yes) _a=OK ;; esac
  V_GIT=$(worst "$_w" "$_g")
  V_ARCHIVE=$(worst "$_w" "$_a")
  if [ "$F_OS" = Darwin ]; then V_GIT=FAIL; V_ARCHIVE=FAIL; fi
  V_DOTNET=$(st dotnet)
  V_DOCKER=$(st docker)
  V_OVERNIGHT=$(worst "$(st power.sleep)" "$(st reboot.pending)")
}

eval_machine() { # sets M_* ; $1 = hours or empty, $2 = 1 when redacting
  M_NAME=old-pc
  [ "$2" != 1 ] && M_NAME=$(safe_name "$F_HOST")
  M_SSH="<user>@<hostname>.lan"
  if [ "$2" != 1 ]; then
    M_SSH="<user>@$M_NAME.lan"
    valid_ssh_user "$F_USER" && M_SSH="$F_USER@$M_NAME.lan"
  fi
  M_OS=linux; [ "$F_OS" = Darwin ] && M_OS=macos-unsupported
  M_CAPS=""
  sdk_ge8 "$F_DOTNET" && M_CAPS="dotnet"
  for _p in node:node python:python java:java go:go rustc:rust; do
    eval "_v=\${F_T_${_p%%:*}:-}"
    [ -n "$_v" ] && M_CAPS="${M_CAPS:+$M_CAPS, }${_p#*:}"
  done
  [ -n "$F_DOCKER_SERVER" ] && M_CAPS="${M_CAPS:+$M_CAPS, }docker"
  M_CAPS="${M_CAPS:+$M_CAPS, }linux"
  M_JOBS=1; [ "${F_CORES:-0}" -ge 8 ] && [ "${F_RAMMB:-0}" -ge 16000 ] && M_JOBS=2
  M_TRANSFER=none
  if [ -n "$F_GIT" ]; then M_TRANSFER=git
  else case "$F_TARKIND:$F_GZIP" in gnu:yes|bsd:yes) M_TRANSFER=archive ;; esac; fi
  M_HOURS=$1
}

render_snippet() {
  if [ "$F_OS" = Darwin ]; then
    echo "# macOS: LanQueue v0.3 workers are windows|linux only, so there is no machines.yml entry for this Mac."
    return
  fi
  if [ "$V_GIT" = FAIL ] && [ "$V_ARCHIVE" = FAIL ]; then echo "# NOT READY yet: fix the FAIL items above first, then re-run."; fi
  echo "machines:"
  echo "  - name: \"$M_NAME\""
  echo "    os: $M_OS"
  echo "    ssh: \"$M_SSH\"   # placeholder: confirm user and host name"
  echo "    work_root: \"/srv/lq\"   # short absolute path; create it, owned by the build user"
  echo "    capabilities: [$M_CAPS]"
  echo "    max_jobs: $M_JOBS"
  if [ "$M_TRANSFER" = none ]; then echo "    # transfer: needs git (transfer: git) or GNU tar/bsdtar + gzip (transfer: archive); neither found"
  else echo "    transfer: $M_TRANSFER"; fi
  [ -n "$M_HOURS" ] && echo "    allowed_hours: \"$M_HOURS\"   # evaluated in the ORCHESTRATOR's local time"
  return 0
}

render_human() {
  echo "lanq-makeready $TOOL_VERSION (read-only: nothing is installed, changed or sent anywhere)"
  if [ "$QUIET" != 1 ]; then
    echo
    printf '%s' "$CHECKS" | while IFS="$SEP" read -r _i _ti _s _d _a _adm; do
      printf '[%-4s] %s: %s\n' "$_s" "$_ti" "$_d"
      [ -n "$_a" ] && [ "$_s" != SKIP ] && printf '       advice: %s\n' "$_a"
      [ "$_s" = SKIP ] && [ "$_adm" = true ] && echo "       skipped (needs admin); re-run as root to fill this in"
    done
  fi
  echo
  echo "Verdicts"
  printf '  %-15s %s\n' worker_git "$V_GIT" worker_archive "$V_ARCHIVE" dotnet10 "$V_DOTNET" docker "$V_DOCKER" overnight "$V_OVERNIGHT"
  if [ "$QUIET" != 1 ]; then
    echo
    echo "# Paste into machines.yml (confirm the placeholders):"
    render_snippet
  fi
  if [ "$V_GIT" = FAIL ] && [ "$V_ARCHIVE" = FAIL ]; then
    echo
    echo "Blockers:"
    printf '%s' "$CHECKS" | while IFS="$SEP" read -r _i _ti _s _d _; do
      [ "$_s" = FAIL ] && printf '  - %s: %s\n' "$_ti" "$_d"
    done
    [ -z "$F_GIT" ] && [ "$F_TARKIND" != gnu ] && [ "$F_TARKIND" != bsd ] && echo "  - neither git nor a usable tar found: no transfer mode works"
  fi
  return 0
}

json_esc() { printf '%s' "$1" | LC_ALL=C tr -c ' -~' '?' | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; } # ASCII only: control chars, ESC, CR and non-UTF-8 bytes become '?'

render_json() { # $1 = 1 when redacting
  _h=$F_HOST; _u=$F_USER
  if [ "$1" = 1 ]; then _h="<redacted>"; _u="<redacted>"; fi
  printf '{\n  "schema": "lanq-makeready/v1",\n  "tool": {"name": "lanq-makeready", "version": "%s"},\n' "$TOOL_VERSION"
  printf '  "generatedAt": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf '  "host": {"hostname": "%s", "user": "%s", "os": "%s", "arch": "%s"},\n' "$(json_esc "$_h")" "$(json_esc "$_u")" "$(json_esc "${F_OSNAME:-$F_OS}")" "$(json_esc "$F_ARCH")"
  printf '  "verdicts": {"worker_git": "%s", "worker_archive": "%s", "dotnet10": "%s", "docker": "%s", "overnight": "%s"},\n' "$V_GIT" "$V_ARCHIVE" "$V_DOTNET" "$V_DOCKER" "$V_OVERNIGHT"
  printf '  "checks": [\n'
  _first=1
  while IFS="$SEP" read -r _i _ti _s _d _a _adm; do
    [ -z "$_i" ] && continue
    [ "$_first" = 1 ] || printf ',\n'
    _first=0
    printf '    {"id": "%s", "title": "%s", "status": "%s", "detail": "%s", "advice": "%s", "needsAdmin": %s}' "$_i" "$(json_esc "$_ti")" "$_s" "$(json_esc "$_d")" "$(json_esc "$_a")" "${_adm:-false}"
  done <<EOF
$CHECKS
EOF
  printf '\n  ],\n'
  printf '  "capabilities": [%s],\n' "$(printf '%s' "$M_CAPS" | sed -e 's/, linux$//' -e 's/^linux$//' | awk -F', ' 'NF { for (i = 1; i <= NF; i++) printf "%s\"%s\"", (i == 1 ? "" : ", "), $i }')"
  printf '  "suggestedMachine": {"name": "%s", "os": "%s", "ssh": "%s", "work_root": "/srv/lq", "capabilities": [%s], "max_jobs": %s, "transfer": "%s"' "$(json_esc "$M_NAME")" "$M_OS" "$(json_esc "$M_SSH")" "$(printf '%s' "$M_CAPS" | awk -F', ' 'NF { for (i = 1; i <= NF; i++) printf "%s\"%s\"", (i == 1 ? "" : ", "), $i }')" "$M_JOBS" "$M_TRANSFER"
  [ -n "$M_HOURS" ] && printf ', "allowed_hours": "%s"' "$M_HOURS"
  printf '}\n}\n'
}

# ---------- probes (read-only) ----------

have() { command -v "$1" >/dev/null 2>&1; }

# macOS ships stubs for git/python3/java that pop up an installer dialog: probe only when the real tool exists.
mac_probe_ok() {
  [ "$F_OS" = Darwin ] || return 0
  case "$1" in
    git|python3) xcode-select -p >/dev/null 2>&1 ;;
    java) /usr/libexec/java_home >/dev/null 2>&1 ;;
    *) return 0 ;;
  esac
}

# Probes run from / with toolchain auto-download and prompts switched off.

run_t() { # run_t SECONDS cmd args... : bounded, stdin closed, never hangs the script
  _secs=$1; shift
  if have timeout; then timeout "$_secs" sh -c 'cd / || exit 1; GOTOOLCHAIN=local; RUSTUP_AUTO_INSTALL=0; GIT_TERMINAL_PROMPT=0; DOCKER_CLI_HINTS=false; export GOTOOLCHAIN RUSTUP_AUTO_INSTALL GIT_TERMINAL_PROMPT DOCKER_CLI_HINTS; exec "$@"' sh "$@" </dev/null; return $?; fi
  if have gtimeout; then gtimeout "$_secs" sh -c 'cd / || exit 1; GOTOOLCHAIN=local; RUSTUP_AUTO_INSTALL=0; GIT_TERMINAL_PROMPT=0; DOCKER_CLI_HINTS=false; export GOTOOLCHAIN RUSTUP_AUTO_INSTALL GIT_TERMINAL_PROMPT DOCKER_CLI_HINTS; exec "$@"' sh "$@" </dev/null; return $?; fi
  sh -c 'cd / || exit 1; GOTOOLCHAIN=local; RUSTUP_AUTO_INSTALL=0; GIT_TERMINAL_PROMPT=0; DOCKER_CLI_HINTS=false; export GOTOOLCHAIN RUSTUP_AUTO_INSTALL GIT_TERMINAL_PROMPT DOCKER_CLI_HINTS; exec "$@"' sh "$@" </dev/null &
  _p=$!
  ( sleep "$_secs"; kill "$_p" ) >/dev/null 2>&1 &
  _k=$!
  wait "$_p" 2>/dev/null
  _r=$?
  kill "$_k" >/dev/null 2>&1
  return "$_r"
}

first_line() { printf '%s\n' "$1" | sed -n '/[^[:space:]]/{p;q;}' | sed -e 's/^[[:space:]]*//'; }

dir_mb() { # bounded folder size in MB, empty when absent
  [ -d "$1" ] || return 0
  _k=$(run_t 5 du -sk "$1" 2>/dev/null | awk '{print $1}')
  [ -n "$_k" ] && echo $(( _k / 1024 ))
}

gather_facts() {
  F_OS=$(uname -s); F_ARCH=$(uname -m); F_HOST=$(hostname 2>/dev/null); F_USER=$(id -un 2>/dev/null)
  F_OSNAME=""; F_OSVER=""; F_LIBC=""; F_LIBCVER=""; F_DISK=Unknown; F_SLEEP=unknown; F_REBOOT=no
  if [ "$F_OS" = Darwin ]; then
    F_OSVER=$(sw_vers -productVersion 2>/dev/null | sed 's/\..*//'); F_OSNAME="macOS $(sw_vers -productVersion 2>/dev/null)"
    F_RAMMB=$(( $(sysctl -n hw.memsize 2>/dev/null || echo 0) / 1048576 ))
    F_FREERAMMB=$(vm_stat 2>/dev/null | awk '/Pages free/ {gsub("\\.","",$3); print int($3*4096/1048576)}')
    F_CORES=$(sysctl -n hw.logicalcpu 2>/dev/null); F_CPU=$(sysctl -n machdep.cpu.brand_string 2>/dev/null); F_AVX2=n/a
    _ps=$(pmset -g 2>/dev/null | awk '$1 == "sleep" {print $2}')
    case "$_ps" in 0) F_SLEEP=never ;; ?*) F_SLEEP=sleeps; F_SLEEP_DETAIL="sleeps after $_ps min" ;; esac
    F_SSHD=unknown; launchctl list 2>/dev/null | grep -q 'com.openssh.sshd' && F_SSHD=running
    F_STARTUP=$(find "$HOME/Library/LaunchAgents" -maxdepth 1 -name '*.plist' 2>/dev/null | wc -l | tr -d ' ')
  else
    F_OSNAME=$(sed -n 's/^PRETTY_NAME=//p' /etc/os-release 2>/dev/null | tr -d '"' | head -n 1)
    F_RAMMB=$(ram_installed_mb "$(awk '/^MemTotal:/ {print int($2/1024)}' /proc/meminfo 2>/dev/null)")
    F_FREERAMMB=$(awk '/^MemAvailable:/ {print int($2/1024)}' /proc/meminfo 2>/dev/null)
    F_CORES=$(getconf _NPROCESSORS_ONLN 2>/dev/null)
    F_CPU=$(sed -n 's/^model name[[:space:]]*:[[:space:]]*//p' /proc/cpuinfo 2>/dev/null | head -n 1)
    if grep -qw avx2 /proc/cpuinfo 2>/dev/null; then F_AVX2=yes; else F_AVX2=no; fi
    _g=$(getconf GNU_LIBC_VERSION 2>/dev/null)
    case "$_g" in
      glibc*) F_LIBC=glibc; F_LIBCVER=${_g#glibc } ;;
      *) _l=$(ldd --version 2>&1 | head -n 1)
         case "$_l" in
           *musl*) F_LIBC=musl; F_LIBCVER=$(ldd --version 2>&1 | sed -n 's/^Version[[:space:]]*//p' | head -n 1) ;;
           *) _l=$(run_t 5 ldd /bin/sh 2>&1); case "$_l" in *musl*) F_LIBC=musl; F_LIBCVER=$(ldd --version 2>&1 | sed -n 's/^Version[[:space:]]*//p' | head -n 1) ;; esac ;;
         esac ;;
    esac
    if have pgrep && pgrep -x sshd >/dev/null 2>&1; then F_SSHD=running
    elif have systemctl && run_t 5 systemctl is-active --quiet ssh 2>/dev/null; then F_SSHD=running
    elif have systemctl && run_t 5 systemctl is-active --quiet sshd 2>/dev/null; then F_SSHD=running
    elif have systemctl && { run_t 5 systemctl is-active --quiet ssh.socket || run_t 5 systemctl is-active --quiet sshd.socket; } 2>/dev/null; then F_SSHD=socket
    elif have sshd || [ -x /usr/sbin/sshd ]; then F_SSHD=stopped
    else F_SSHD=notinstalled; fi
    _hdd=0; _ssd=0
    for _q in /sys/block/*/queue/rotational; do
      case "$_q" in */loop*|*/ram*|*/zram*|*/dm-*|*/sr*|*/nbd*) continue ;; esac
      [ -r "$_q" ] || continue
      if [ "$(cat "$_q")" = 1 ]; then _hdd=1; else _ssd=1; fi
    done
    if [ "$_ssd" = 1 ]; then F_DISK=SSD; elif [ "$_hdd" = 1 ]; then F_DISK=HDD; fi
    if have systemctl && [ "$(run_t 5 systemctl is-enabled sleep.target 2>/dev/null)" = masked ]; then F_SLEEP=masked
    elif have gsettings && have_session_bus; then
      _gt=$(run_t 5 gsettings get org.gnome.settings-daemon.plugins.power sleep-inactive-ac-type 2>/dev/null)
      case "$_gt" in *nothing*) F_SLEEP=never ;; ?*) F_SLEEP=sleeps; F_SLEEP_DETAIL="GNOME idle action $_gt on AC" ;; esac
    fi
    [ -e /var/run/reboot-required ] && F_REBOOT=yes
    if have systemctl && have_session_bus; then F_STARTUP=$(run_t 5 systemctl --user list-unit-files --state=enabled 2>/dev/null | grep -c '\.service'); fi
    if [ -e /sys/fs/cgroup/cgroup.controllers ]; then F_CGROUP=v2; elif [ -d /sys/fs/cgroup ]; then F_CGROUP=v1; fi
  fi
  F_SYSFREEGB=$(( $(df -Pk / 2>/dev/null | awk 'NR==2 {print $4}') / 1048576 ))
  _wd=/srv; [ -d /srv ] || _wd=/
  F_WORKFREEGB=$(( $(df -Pk "$_wd" 2>/dev/null | awk 'NR==2 {print $4}') / 1048576 ))
  if [ -s "$HOME/.ssh/authorized_keys" ]; then F_AUTHKEYS=yes; else F_AUTHKEYS=no; fi
  F_GIT=""; if have git && mac_probe_ok git; then F_GIT=$(first_line "$(run_t 8 git --version 2>/dev/null)"); fi
  F_TARKIND=none; if have tar; then F_TARKIND=$(classify_tar "$(run_t 8 tar --version 2>&1)"); fi
  if have gzip; then F_GZIP=yes; else F_GZIP=no; fi
  F_DOTNET=""; _dn=/nonexistent
  if have dotnet; then _dp=$(command -v dotnet); _dp=$(readlink -f "$_dp" 2>/dev/null || echo "$_dp"); _dn=${_dp%/*}; fi
  for _d in "${DOTNET_ROOT:-/nonexistent}" /usr/share/dotnet /usr/lib/dotnet /usr/lib64/dotnet /usr/local/share/dotnet "$HOME/.dotnet" "$_dn"; do
    [ -d "$_d/sdk" ] || continue
    for _s in "$_d"/sdk/[0-9]*; do
      [ -d "$_s" ] || continue
      _m=${_s##*/}; _m=${_m%%.*}
      case " $F_DOTNET " in *" $_m "*) ;; *) F_DOTNET="${F_DOTNET:+$F_DOTNET }$_m" ;; esac
    done
  done
  F_DOCKER_CLI=no; F_DOCKER_SERVER=""; F_DOCKER_REMOTE=no
  _dctx=${DOCKER_CONTEXT:-}
  if [ -z "$_dctx" ] && have docker; then _dctx=$(first_line "$(run_t 5 docker context show 2>/dev/null)"); fi
  if docker_remote "${DOCKER_HOST:-}" "$_dctx"; then F_DOCKER_REMOTE=yes; fi
  if have docker; then
    F_DOCKER_CLI=yes
    [ "$F_DOCKER_REMOTE" = yes ] || F_DOCKER_SERVER=$(first_line "$(run_t 10 docker version --format '{{.Server.Version}}' 2>/dev/null)")
  fi
  for _t in node:--version python3:--version java:-version go:version rustc:--version; do
    _n=${_t%%:*}; _a=${_t#*:}
    if ! have "$_n" || ! mac_probe_ok "$_n"; then continue; fi
    _o=$(first_line "$(run_t 8 "$_n" "$_a" 2>&1)")
    [ "$_n" = python3 ] && _n=python
    [ -n "$_o" ] && eval "F_T_$_n=\$_o"
  done
  _tm=""; F_TEMP_BIGMB=0
  for _p in "cache:$HOME/.cache" "NuGet packages:$HOME/.nuget/packages" "npm cache:$HOME/.npm/_cacache"; do
    _mb=$(dir_mb "${_p#*:}")
    [ -n "$_mb" ] || continue
    _tm="${_tm:+$_tm; }${_p%%:*} ${_mb} MB"
    [ "$_mb" -gt "$F_TEMP_BIGMB" ] && F_TEMP_BIGMB=$_mb
  done
  F_TEMP=$_tm
}

# ---------- main ----------

main() {
  JSON_PATH=""; REDACT=0; HOURS=""; QUIET=0
  while [ $# -gt 0 ]; do
    case "$1" in
      --json) [ $# -ge 2 ] || { echo "lanq-makeready: --json needs a path (or - for stdout)" >&2; exit 2; }; JSON_PATH=$2; shift ;;
      --redact) REDACT=1 ;;
      --hours) [ $# -ge 2 ] || { echo "lanq-makeready: --hours needs a value" >&2; exit 2; }; HOURS=$2; shift ;;
      --quiet) QUIET=1 ;;
      -h|--help) echo "usage: lanq-makeready.sh [--json PATH|-] [--redact] [--hours HH:MM-HH:MM] [--quiet]"; exit 0 ;;
      *) echo "lanq-makeready: unknown argument: $1 (usage: [--json PATH|-] [--redact] [--hours HH:MM-HH:MM] [--quiet])" >&2; exit 2 ;;
    esac
    shift
  done
  if [ -n "$HOURS" ] && ! valid_hours "$HOURS"; then
    echo "lanq-makeready: --hours must look like 00:00-07:00 (HH:MM-HH:MM, start different from end)." >&2
    exit 2
  fi
  gather_facts
  [ "$REDACT" = 1 ] && redact_facts
  eval_checks
  eval_verdicts
  eval_machine "$HOURS" "$REDACT"
  if [ "$JSON_PATH" = "-" ]; then render_json "$REDACT"
  else
    render_human
    [ -n "$JSON_PATH" ] && render_json "$REDACT" > "$JSON_PATH"
  fi
  if [ "$V_GIT" != FAIL ] || [ "$V_ARCHIVE" != FAIL ]; then exit 0; fi
  exit 1
}

# LANQ_MAKEREADY_LIB=1 is for tests that source this file AND define __lanq_makeready_test_harness first.
# shellcheck disable=SC2317
if [ "${LANQ_MAKEREADY_LIB:-}" = 1 ]; then
  if type __lanq_makeready_test_harness >/dev/null 2>&1; then return 0 2>/dev/null || exit 0; fi
  echo "lanq-makeready: LANQ_MAKEREADY_LIB=1 is only for the test runner (it must source this file); unset it." >&2
  exit 2
fi
# stdin is closed for everything main runs, so nothing can consume the script when it arrives via 'sh -s'
main "$@" </dev/null
