#!/bin/sh
# lanq-makeready: READ-ONLY check of whether this Linux/macOS machine can be a LanQueue worker.
# Changes nothing, installs nothing, makes no network calls, needs no root.
# Every fix is printed as advice for you to run yourself (or not).
#
# Usage: lanq-makeready.sh [--json PATH|-] [--out FILE] [--redact] [--hours HH:MM-HH:MM] [--quiet]
#        --json - writes ONLY the JSON to stdout (for running over SSH: sh -s -- --json - --redact < this-file)
#        --out FILE also saves the human report to FILE (a new file only: an existing file is never overwritten)
# Exit:  0 = worker-ready, 1 = not ready, 2 = bad arguments.

TOOL_VERSION=0.3.0
SEP=$(printf '\037')
CHECKS=""

# BEGIN ADVICE
# The ONE table of advice text. Nothing here is ever executed by this script.
advice() {
  case "$1" in
    ssh.install) echo "Install and start an SSH server yourself, for example (Debian/Ubuntu): sudo apt install openssh-server ; sudo systemctl enable --now ssh" ;;
    ssh.start) echo "sshd is installed but not running. Start it yourself: sudo systemctl enable --now ssh   (the unit is called sshd on Fedora/RHEL)" ;;
    ssh.nokeys) echo "As the build user: mkdir -p ~/.ssh ; chmod 700 ~/.ssh ; append the orchestrator public key (one line) to ~/.ssh/authorized_keys ; chmod 600 ~/.ssh/authorized_keys . Fine to ignore if another user is the build user." ;;
    ssh.password) echo "Security note: sshd accepts passwords. Once key login works, make it key-only: sudoedit /etc/ssh/sshd_config.d/10-key-only.conf and add the two lines PasswordAuthentication no and KbdInteractiveAuthentication no (no sshd_config.d folder: put them near the top of /etc/ssh/sshd_config), then: sudo sshd -t && sudo systemctl reload ssh   (the unit is sshd on Fedora/RHEL)" ;;
    os.eol) echo "Standard security support for this release has ended (Ubuntu LTS: Ubuntu Pro/ESM may still cover it, check with: pro status ; Debian: Debian LTS/ELTS may cover older releases). It still works for LanQueue, but upgrade when you can: Ubuntu: sudo do-release-upgrade (to a supported LTS such as 24.04) ; Debian: follow the release notes for Debian 13 ; or reinstall a supported release." ;;
    ssh.mac) echo "On macOS check System Settings > General > Sharing > Remote Login yourself. Note: LanQueue v0.3 workers are windows|linux only." ;;
    git.install) echo "git is only needed for transfer: git. Without it use transfer: archive, or install git with your package manager." ;;
    tar.busybox) echo "busybox tar (Alpine, minimal images) is refused by LanQueue for transfer: archive. Install GNU tar or bsdtar, for example on Alpine: apk add tar" ;;
    tar.missing) echo "transfer: archive needs GNU tar or bsdtar, plus gzip. Install them with your package manager." ;;
    docker.remote) echo "A remote docker endpoint (DOCKER_HOST or a non-local context) is never probed." ;;
    os.mac) echo "LanQueue v0.3 workers are windows|linux only, so this Mac cannot be a worker yet." ;;
    arch32) echo "32-bit x86 Linux is not supported by .NET 10. Install a 64-bit OS (the CPU may support it)." ;;
    ram.low) echo "Less than 4 GB RAM: builds will swap and be slow. In machines.yml keep max_jobs: 1 and min_free_mem_gb (LanQueue holds the PC while less memory is free), and cap build tools with the env: lines in the snippet. Below 2 GB .NET SDK builds are unreliable." ;;
    swap.none) echo "No (or tiny) swap: a build that runs out of memory is killed. Add a swap file: sudo fallocate -l 4G /swapfile && sudo chmod 600 /swapfile && sudo mkswap /swapfile && sudo swapon /swapfile , then add the line '/swapfile none swap sw 0 0' to /etc/fstab (or install zram-tools)." ;;
    disk.hdd) echo "A spinning disk makes builds slow. An SSD (even a small cheap one) is the single best upgrade for an old PC." ;;
    disk.low) echo "Free disk space is low. Each job needs room for a worktree plus build output; keep at least 20 GB free, below 10 GB the worker is refused. Find space: sudo apt clean ; du -xh -d1 ~ | sort -h ; or use a bigger disk." ;;
    dotnet.glibc) echo "The .NET 10 SDK needs glibc 2.27 or newer (musl 1.2.3 or newer on Alpine). Use a newer distribution release (Ubuntu 22.04/24.04, Debian 13, Alpine 3.21+)." ;;
    dotnet.mac) echo ".NET 10 supports macOS 15 and newer." ;;
    dotnet.path) echo "The newest .NET SDK is in a folder that is not on PATH for this account, so a job running dotnet gets another one (or none). In machines.yml add path_prepend: [that folder] and env: DOTNET_ROOT: that folder (the snippet below has both; LanQueue 0.8+). If the step says other local users can write there, the snippet leaves it out: first let only root and the build account write to that folder, then run this again. Nothing on this machine is changed." ;;
    docker.daemon) echo "The docker client is installed but the engine is not answering (or you lack permission). Start the service or check group membership yourself." ;;
    docker.ram) echo "Under 4 GB RAM: containers are not recommended on this machine." ;;
    power.desktop) echo "A desktop is installed; its power settings cannot be read from this session (no desktop session bus, e.g. over SSH). GNOME and GDM suspend on AC after about 15-20 minutes by default. Check Settings > Power > Automatic Suspend on the PC, or as the desktop user: gsettings set org.gnome.settings-daemon.plugins.power sleep-inactive-ac-type 'nothing' ; for the GDM login screen: sudo -u gdm dbus-run-session gsettings set org.gnome.settings-daemon.plugins.power sleep-inactive-ac-type 'nothing' ; or block all suspend: sudo systemctl mask sleep.target suspend.target hibernate.target hybrid-sleep.target" ;;
    power.sleep) echo "On a PC that should stay on, stop systemd from suspending it: sudo systemctl mask sleep.target suspend.target hibernate.target hybrid-sleep.target   (undo with systemctl unmask). Skip this if you want the PC to sleep and use LanQueue's wake: setting instead." ;;
    reboot.pending) echo "A restart is pending; the machine may reboot during a job. Restart it at a time you choose: sudo reboot" ;;
    wol.wired) echo "Wake-on-LAN needs a wired Ethernet connection (Wi-Fi rarely supports it). Use a cable, or leave wake: out and keep the PC awake." ;;
    wol.off) echo "Turn magic-packet wake on: sudo ethtool -s IFACE wol g (lasts until reboot). Make it permanent with NetworkManager: sudo nmcli connection modify '<connection name>' 802-3-ethernet.wake-on-lan magic . Also enable Wake on LAN in the BIOS/UEFI. Test: lanq wake <machine> --wait" ;;
    wol.nosupport) echo "This network card does not offer magic-packet wake (no g in Supports Wake-on). Try another wired port (often the onboard one), or leave wake: out." ;;
    startup.many) echo "Many enabled user services slow login and use RAM. Review them yourself with systemctl --user list-unit-files. This tool never disables anything." ;;
    temp.big) echo "Large cache folders. Review before deleting; this tool never deletes anything." ;;
    *) echo "" ;;
  esac
}
advice_admin() { # true when acting on that advice needs root
  case "$1" in ssh.install|ssh.start|ssh.password|power.desktop|tar.busybox|tar.missing|os.eol|power.sleep|wol.off|swap.none|reboot.pending|dotnet.glibc|arch32|docker.daemon|git.install) echo true ;; *) echo false ;; esac
}
# END ADVICE

# ---------- pure helpers (no I/O): used by tests with fixtures ----------

add_check() { # id title status detail [advice-key[:iface]] [needs-admin-to-read]; IFACE in the advice becomes iface
  _adv=""; _fx=false
  if [ -n "$5" ]; then
    _adv=$(advice "${5%%:*}"); _fx=$(advice_admin "${5%%:*}")
    case "$5" in *:*) _sub=${5#*:}
      while true; do case "$_adv" in *IFACE*) _adv="${_adv%%IFACE*}$_sub${_adv#*IFACE}" ;; *) break ;; esac; done ;; esac
  fi
  CHECKS="${CHECKS}$1${SEP}$2${SEP}$3${SEP}$4${SEP}${_adv}${SEP}${6:-false}${SEP}${_fx}
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
  F_OSNAME=$(rv "$F_OSNAME"); F_CPU=$(rv "$F_CPU"); F_GIT=$(rv "$F_GIT"); F_DOCKER_SERVER=$(rv "$F_DOCKER_SERVER"); F_TEMP=$(rv "$F_TEMP"); F_DOTNET_DIR=$(rv "$F_DOTNET_DIR")
  for _n in node python java go rustc; do
    eval "_v=\${F_T_$_n:-}"
    [ -n "$_v" ] || continue
    _rv=$(rv "$_v")
    eval "F_T_$_n=\$_rv"
  done
  F_HOST="<redacted>"; F_USER="<redacted>"
  [ -n "${F_WOL_MAC:-}" ] && F_WOL_MAC="<mac>"
  return 0
}

classify_tar() { # text of tar --version (stderr included) -> gnu | bsd | busybox | none
  case "$1" in
    *"GNU tar"*) echo gnu ;;
    *bsdtar*) echo bsd ;;
    *[Bb]usy[Bb]ox*) echo busybox ;;
    *) echo none ;;
  esac
}

distro_eol() { # $1 = os-release ID, $2 = VERSION_ID -> last month with standard security support (YYYYMM), "old" = older than this table, empty = unknown
  _M=${2%%.*}; _m=${2#*.}; [ "$_m" = "$2" ] && _m=0; _m=${_m%%.*}; case "$_m" in 0?*) _m=${_m#0} ;; esac
  case "$_M:$_m" in *[!0-9:]*|:*|*:) return 0 ;; esac
  case "$1" in
    ubuntu) _y=$((2000 + _M))
      if [ "$_M" -lt 16 ]; then echo old
      elif [ "$_M" = 16 ] && [ "$_m" = 4 ]; then echo 202104
      elif [ "$_m" = 4 ] && [ $((_M % 2)) = 0 ]; then _y=$((_y + 5)); echo "$((_y * 100 + 5))" # LTS: 5 years
      else _t=$((_y * 12 + _m + 8)); _y=$((_t / 12)); echo "$((_y * 100 + _t % 12 + 1))"; fi ;; # interim: 9 months
    debian|raspbian) case "$_M" in 10) echo 202406 ;; 11) echo 202608 ;; 12) echo 202806 ;; 13) echo 203006 ;; *) [ "$_M" -lt 10 ] && echo old ;; esac ;;
    alpine) [ "$_M" = 3 ] || return 0 # official end dates (alpinelinux.org/releases) are the 1st of a month: the month before is the last
      case "$_m" in 17) echo 202410 ;; 18) echo 202504 ;; 19) echo 202510 ;; 20) echo 202603 ;; 21) echo 202610 ;; 22) echo 202704 ;; 23) echo 202710 ;; *) [ "$_m" -lt 17 ] && echo old ;; esac ;;
    fedora) case "$_M" in 39) echo 202411 ;; 40) echo 202505 ;; 41) echo 202512 ;; 42) echo 202605 ;; 43) echo 202612 ;; *) [ "$_M" -lt 39 ] && echo old ;; esac ;;
    centos) case "$_M" in 7) echo 202406 ;; 8) echo 202112 ;; 9) echo 202705 ;; esac ;;
    linuxmint) case "$_M" in 19) echo 202304 ;; 20) echo 202504 ;; 21) echo 202704 ;; 22) echo 202904 ;; esac ;;
    *) ;;
  esac
  return 0
}

pick_wired() { # lines "name type has-device wireless operstate mac" -> "name mac" of a physical wired NIC (an up one first)
  _h='[0-9a-f][0-9a-f]'
  awk -v re="^$_h:$_h:$_h:$_h:$_h:$_h\$" '$1 != "lo" && $2 == "1" && $3 == "yes" && $4 == "no" && $6 ~ re && $6 != "00:00:00:00:00:00" { if ($5 == "up" && u == "") u = $1 " " $6; else if (f == "") f = $1 " " $6 } END { if (u != "") print u; else if (f != "") print f }'
}

sshd_pw_auth() { # sshd config text on stdin -> yes | no | unset (the first value wins, as in sshd; Match blocks are not evaluated)
  # Reads to the end instead of exiting early: an early exit closes the pipe while sshd_conf_lines is still writing, and
  # dash then prints "printf: I/O error" once per remaining line (seen on GitHub's Ubuntu runner).
  awk 'BEGIN { v = "unset" } d { next } { sub(/^[ \t]+/, ""); n = split($0, a, /[ \t=]+/); k = tolower(a[1]) } k == "match" { d = 1; next } k == "passwordauthentication" && n >= 2 { v = tolower(a[2]); d = 1 } END { print v }'
}

logind_values() { # logind.conf text (main file, then drop-ins) on stdin -> "IdleAction HandleLidSwitch HandleLidSwitchExternalPower", "-" when unset; the last value wins
  awk '/^[ \t]*[#;]/ { next } { gsub(/[ \t]/, "") } /^IdleAction=/ { i = substr($0, 12) } /^HandleLidSwitch=/ { l = substr($0, 17) } /^HandleLidSwitchExternalPower=/ { e = substr($0, 30) } END { print (i == "" ? "-" : i) " " (l == "" ? "-" : l) " " (e == "" ? "-" : e) }'
}

classify_sleep() { # $1 logind finding, $2 GNOME sleep-inactive-ac-type ("" = not read), $3 display manager active, $4 session bus (yes|no) -> "state|detail"
  if [ -n "$1" ]; then echo "sleeps|$1"; return 0; fi
  if [ "$4" = yes ] && [ -n "$2" ]; then
    case "$2" in *nothing*) echo "unmasked|logind idle and lid, this user's GNOME setting" ;; *) echo "sleeps|GNOME idle action $2 on AC (this user)" ;; esac
    return 0
  fi
  if [ "$3" = yes ]; then echo "desktop|"; return 0; fi
  echo "unmasked|logind idle and lid"
}

logind_auto_suspend() { # $1 idle $2 lid $3 lid-on-AC $4 laptop(yes|no) -> what would suspend this PC, empty = nothing found
  case "$1" in suspend|hibernate|hybrid-sleep|suspend-then-hibernate|sleep) echo "logind IdleAction=$1"; return 0 ;; esac
  [ "$4" = yes ] || return 0
  _la=$3; [ "$_la" = - ] && _la=$2
  case "$_la" in
    -) echo "laptop lid: HandleLidSwitch default (suspend)" ;;
    suspend|hibernate|hybrid-sleep|suspend-then-hibernate|sleep) echo "laptop lid: HandleLidSwitch=$_la" ;;
  esac
  return 0
}

# Pool sizing, the thresholds LanQueue 0.5.1+ lanq health uses. $1 = RAM in whole GB, $2 = logical CPUs (24-31 GB counts as the top band).
suggest_jobs() {
  if [ "$1" -lt 6 ]; then _j=1; elif [ "$1" -lt 12 ]; then _j=2
  elif [ "$1" -lt 24 ]; then _j=3; [ "$2" -ge 8 ] && _j=4
  else _j=6; [ "$2" -ge 16 ] && _j=8; fi
  [ "$2" -ge 1 ] && [ "$_j" -gt "$2" ] && _j=$2
  echo "$_j"
}
suggest_minmem() { if [ "$1" -lt 6 ]; then echo 1.5; else echo 2; fi; } # headroom for one more job
suited_tier() { # $1 = RAM GB, $2 = logical CPUs
  if [ "$1" -lt 6 ]; then echo light; elif [ "$1" -ge 16 ] && [ "$2" -ge 4 ]; then echo heavy; else echo medium; fi
}
suited_text() { # $1 = tier, $2 = yes when Docker works
  case "$1" in light) _s="light: lint and unit-test shards" ;; heavy) _s="heavy: full suites and large builds" ;; *) _s="medium: test shards and builds" ;; esac
  [ "$2" = yes ] && _s="$_s + container builds"
  echo "$_s"
}

eval_checks() { # reads F_* facts, fills CHECKS
  CHECKS=""
  _os="${F_OSNAME:-$F_OS}"
  if [ "$F_OS" = Darwin ]; then add_check os.version OS FAIL "$_os (macOS): LanQueue workers are windows|linux only" os.mac
  else
    _eol=$(distro_eol "${F_OSID:-}" "${F_OSVERID:-}")
    if [ "$_eol" = old ]; then add_check os.version OS WARN "$_os: end of life (older than this table)" os.eol
    elif [ -n "$_eol" ] && [ "${F_NOW_YM:-0}" -gt "$_eol" ]; then add_check os.version OS WARN "$_os: end of life: standard security support ended after ${_eol%??}-${_eol#????}" os.eol
    else add_check os.version OS OK "$_os"; fi
  fi
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
  _dd="system drive $F_SYSFREEGB GB free, work drive $F_WORKFREEGB GB free, disk type: ${F_DISK:-Unknown}"
  [ "$_da" = disk.low ] && _dd="$_dd (recommended: at least 20 GB free; under 10 GB is refused)"
  add_check disk Disk "$_ds" "$_dd" "$_da"
  case "$F_SSHD" in
    running) add_check ssh.server "SSH server" OK "sshd running" ;;
    socket) add_check ssh.server "SSH server" OK "sshd is socket-activated (starts on the first connection)" ;;
    stopped) add_check ssh.server "SSH server" FAIL "sshd installed but not running" ssh.start ;;
    notinstalled) add_check ssh.server "SSH server" FAIL "no sshd found" ssh.install ;;
    *) add_check ssh.server "SSH server" SKIP "could not determine without admin rights" ssh.mac true ;;
  esac
  case "$F_SSHD:${F_SSHD_PW:-unknown}" in
    notinstalled:*) ;;
    *:yes) add_check ssh.password "SSH passwords" WARN "PasswordAuthentication yes in the sshd config (security note)" ssh.password ;;
    *:unset) add_check ssh.password "SSH passwords" WARN "PasswordAuthentication is not set, so sshd's default (yes) applies (security note)" ssh.password ;;
    *:no) add_check ssh.password "SSH passwords" OK "key-only (PasswordAuthentication no)" ;;
    *) add_check ssh.password "SSH passwords" SKIP "sshd config not readable" ;;
  esac
  if [ "$F_AUTHKEYS" = yes ]; then
    case "$F_AUTHVIA" in
      match) add_check ssh.keys "SSH keys" OK "authorized keys present and non-empty (the file a Match block in sshd_config names)" ;;
      global) add_check ssh.keys "SSH keys" OK "authorized keys present and non-empty (the file sshd_config names)" ;;
      *) add_check ssh.keys "SSH keys" OK "authorized_keys present and non-empty" ;;
    esac
  elif [ "$F_AUTHVIA" = unreadable ]; then add_check ssh.keys "SSH keys" WARN "no ~/.ssh/authorized_keys, and sshd_config is not readable, so keys may be set up elsewhere" ssh.nokeys
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
    unmasked) add_check power.sleep "Sleep/suspend" OK "sleep targets are not masked; no automatic suspend was found (checked: ${F_SLEEP_DETAIL:-logind idle and lid}); masking them makes an always-on PC certain" ;;
    desktop) add_check power.sleep "Sleep/suspend" WARN "a desktop is installed (display manager active) but its power settings cannot be read here; GNOME and GDM suspend on AC after about 15-20 minutes by default" power.desktop ;;
    *) add_check power.sleep "Sleep/suspend" SKIP "could not determine (no pmset, gsettings or systemd)" ;;
  esac
  if [ "$F_REBOOT" = yes ]; then add_check reboot.pending "Pending reboot" WARN "a restart is pending" reboot.pending
  else add_check reboot.pending "Pending reboot" OK "none"; fi
  eval_wol
  _swt="Swap"
  if [ -z "${F_SWAPMB:-}" ]; then add_check swap "$_swt" SKIP "could not read"
  elif [ "${F_RAMMB:-0}" -lt 8192 ] && [ "$F_SWAPMB" -lt 1024 ]; then add_check swap "$_swt" WARN "$(gb "$F_SWAPMB") GB swap with $(gb "${F_RAMMB:-0}") GB RAM" swap.none
  else add_check swap "$_swt" OK "$(gb "$F_SWAPMB") GB swap"; fi
  if [ -z "$F_STARTUP" ]; then add_check startup "Startup items" SKIP "could not read"
  elif [ "$F_STARTUP" -gt 20 ]; then add_check startup "Startup items" WARN "$F_STARTUP enabled user services/agents" startup.many
  else add_check startup "Startup items" OK "$F_STARTUP enabled user services/agents"; fi
  if [ -z "$F_TEMP" ]; then add_check temp "Cache folders" SKIP "none readable"
  elif [ "${F_TEMP_BIGMB:-0}" -ge 5120 ]; then add_check temp "Cache folders" WARN "$F_TEMP" temp.big
  else add_check temp "Cache folders" OK "$F_TEMP"; fi
}

eval_wol() {
  _t="Wake-on-LAN"
  if [ -z "$F_WOL_IF" ]; then
    if [ "$F_WOL_WIFI" = yes ]; then add_check wol "$_t" WARN "Wi-Fi only, no wired Ethernet adapter: Wake-on-LAN needs a cable" wol.wired
    else add_check wol "$_t" WARN "no wired Ethernet adapter found" wol.wired; fi
    return 0
  fi
  _n="wired $F_WOL_IF ($F_WOL_MAC)"
  case "$F_WOL_ON" in
    *g*) add_check wol "$_t" OK "$_n: magic-packet wake is on (Wake-on: $F_WOL_ON); the BIOS/UEFI setting cannot be read, enable it there too" ;;
    ?*) case "$F_WOL_SUP" in
          *g*|"") add_check wol "$_t" WARN "$_n: magic-packet wake is off (Wake-on: $F_WOL_ON)" "wol.off:$F_WOL_IF" ;;
          *) add_check wol "$_t" WARN "$_n: the card does not support magic-packet wake (Supports Wake-on: $F_WOL_SUP)" wol.nosupport ;;
        esac ;;
    *) add_check wol "$_t" SKIP "$_n: Wake-on is not readable without root; check it as root: ethtool $F_WOL_IF | grep Wake-on (g = on)" "" true ;;
  esac
}

eval_dotnet() {
  _t=".NET 10 SDK"
  _have="no SDK found"; [ -n "$F_DOTNET" ] && _have="SDK majors found: $F_DOTNET"
  [ "$F_DOTNET_OFFPATH" = yes ] && _have="$_have; the newest is in $F_DOTNET_DIR, not the dotnet on PATH"
  [ "$F_DOTNET_OPEN" = yes ] && _have="$_have, but other local users can write there, so it is not suggested"
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
  elif [ "$F_DOTNET_OFFPATH" = yes ]; then add_check dotnet "$_t" WARN "$_lib ($_have)" dotnet.path
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

eval_verdicts() { # sets V_* (and T_* = transfer tool alone) from CHECKS and F_*
  _w=$(worst "$(st ssh.server)" "$(st ssh.keys)" "$(st disk)" "$(st os.version)")
  T_GIT=FAIL; [ -n "$F_GIT" ] && T_GIT=OK
  T_ARCHIVE=FAIL; case "$F_TARKIND:$F_GZIP" in gnu:yes|bsd:yes) T_ARCHIVE=OK ;; esac
  V_GIT=$(worst "$_w" "$T_GIT")
  V_ARCHIVE=$(worst "$_w" "$T_ARCHIVE")
  if [ "$F_OS" = Darwin ]; then V_GIT=FAIL; V_ARCHIVE=FAIL; fi
  V_DOTNET=$(st dotnet)
  V_DOCKER=$(st docker)
  V_OVERNIGHT=$(worst "$(st power.sleep)" "$(st reboot.pending)")
}

is_worker_check() { case "$1" in os.version|ssh.server|ssh.keys|disk|git|tar) return 0 ;; *) return 1 ;; esac; }

# One-line verdict plus the numbered next steps: every WARN/FAIL check, blockers first.
eval_summary() { # sets S_VERDICT S_TEXT S_BLOCKERS S_WARNINGS S_STEPS (id SEP title SEP status SEP detail SEP advice SEP admin-to-fix SEP blocker)
  _ready=0; if [ "$V_GIT" != FAIL ] || [ "$V_ARCHIVE" != FAIL ]; then _ready=1; fi
  _sb=""; _sf=""; _sw=""
  while IFS="$SEP" read -r _i _ti _s _d _a _adm _fx; do
    case "$_s" in WARN|FAIL) ;; *) continue ;; esac
    _blk=0
    if [ "$_ready" = 0 ] && is_worker_check "$_i"; then
      if [ "$_s" = FAIL ]; then _blk=1
      elif [ "$T_GIT" = FAIL ] && [ "$T_ARCHIVE" = FAIL ]; then case "$_i" in git|tar) _blk=1 ;; esac; fi
    fi
    _ln="$_i$SEP$_ti$SEP$_s$SEP$_d$SEP$_a$SEP${_fx:-false}$SEP$_blk
"
    if [ "$_blk" = 1 ]; then _sb="$_sb$_ln"; elif [ "$_s" = FAIL ]; then _sf="$_sf$_ln"; else _sw="$_sw$_ln"; fi
  done <<EOF
$CHECKS
EOF
  S_STEPS="$_sb$_sf$_sw"
  S_BLOCKERS=$(printf '%s' "$_sb" | grep -c .)
  S_WARNINGS=$(printf '%s' "$_sf$_sw" | grep -c .)
  if [ "$_ready" = 0 ]; then
    S_VERDICT=NOT_READY; _p=blockers; [ "$S_BLOCKERS" = 1 ] && _p=blocker
    S_TEXT="NOT READY ($S_BLOCKERS $_p)"
  elif [ "$S_WARNINGS" -gt 0 ]; then S_VERDICT=READY_WITH_WARNINGS; S_TEXT="READY WITH WARNINGS ($S_WARNINGS)"
  else S_VERDICT=READY; S_TEXT=READY; fi
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
  M_DOCKER=no; [ -n "$F_DOCKER_SERVER" ] && M_DOCKER=yes
  [ "$M_DOCKER" = yes ] && M_CAPS="${M_CAPS:+$M_CAPS, }docker"
  [ -n "$F_GIT" ] && M_CAPS="${M_CAPS:+$M_CAPS, }git"
  M_CAPS="${M_CAPS:+$M_CAPS, }linux"
  M_RAMGB=$(( ${F_RAMMB:-0} / 1024 )); _cpu=${F_CORES:-1}; case "$_cpu" in ""|*[!0-9]*) _cpu=1 ;; esac
  M_JOBS=$(suggest_jobs "$M_RAMGB" "$_cpu")
  M_MINMEM=$(suggest_minmem "$M_RAMGB")
  M_TIER=$(suited_tier "$M_RAMGB" "$_cpu")
  M_SUITED=$(suited_text "$M_TIER" "$M_DOCKER")
  M_TRANSFER=none
  if [ -n "$F_GIT" ]; then M_TRANSFER=git
  else case "$F_TARKIND:$F_GZIP" in gnu:yes|bsd:yes) M_TRANSFER=archive ;; esac; fi
  M_HOURS=$1
  M_WAKE=no; [ "$(st wol)" = OK ] && M_WAKE=yes
}

render_snippet() {
  if [ "$F_OS" = Darwin ]; then
    echo "# macOS: LanQueue workers are windows|linux only, so there is no machines.yml entry for this Mac."
    return
  fi
  if [ "$V_GIT" = FAIL ] && [ "$V_ARCHIVE" = FAIL ]; then echo "# NOT READY yet: fix the MUST FIX steps first, then re-run."; fi
  echo "machines:"
  echo "  - name: \"$M_NAME\""
  echo "    os: $M_OS"
  echo "    ssh: \"$M_SSH\"   # placeholder: confirm user and host name"
  echo "    work_root: \"/srv/lq\"   # short absolute path; create it, owned by the build user"
  echo "    capabilities: [$M_CAPS]"
  echo "    max_jobs: $M_JOBS   # $M_RAMGB GB RAM, ${F_CORES:-?} CPUs"
  echo "    min_free_mem_gb: $M_MINMEM   # LanQueue starts no new job while less memory than this is free"
  if [ "$M_TRANSFER" = none ]; then echo "    # transfer: needs git (transfer: git) or GNU tar/bsdtar + gzip (transfer: archive); neither found"
  else echo "    transfer: $M_TRANSFER"; fi
  if [ -n "$M_HOURS" ]; then echo "    allowed_hours: \"$M_HOURS\"   # the PC's own clock"
  else echo "    # allowed_hours: \"22:00-07:00\"   # optional: run jobs only overnight (the PC's own clock)"; fi
  if [ -z "$F_WOL_IF" ]; then echo "    # wake: not suggested: no wired Ethernet adapter found"
  elif [ "$M_WAKE" = yes ] && [ "$F_WOL_MAC" = "<mac>" ]; then
    echo "    # wake:   # Wake-on-LAN looks ready; <mac> is a placeholder (redacted): put this PC's wired MAC here and uncomment"
    echo "    #   mac: \"<mac>\""
  elif [ "$M_WAKE" = yes ]; then
    echo "    wake:   # Wake-on-LAN (LanQueue 0.5+); test it: lanq wake $M_NAME --wait"
    echo "      mac: \"$F_WOL_MAC\""
    echo "      # broadcast: 10.20.30.255   # optional: your subnet's directed broadcast on a PC with several networks or a VPN"
  else
    echo "    # wake:   # not ready yet (see the Wake-on-LAN step); uncomment once it is fixed"
    echo "    #   mac: \"$F_WOL_MAC\""
  fi
  _pp=no; [ "$F_DOTNET_OFFPATH" = yes ] && [ "$F_DOTNET_OPEN" = no ] && _pp=yes
  [ "$F_DOTNET_OPEN" = yes ] && echo "    # path_prepend: not suggested: other local users can write to $F_DOTNET_DIR (see the .NET step)"
  if [ "$_pp" = yes ]; then
    echo "    path_prepend: [\"$F_DOTNET_DIR\"]   # the newest .NET SDK is here, not on PATH (LanQueue 0.8+)"
    echo "    env:"
    echo "      DOTNET_ROOT: \"$F_DOTNET_DIR\""
  fi
  if [ "$M_RAMGB" -lt 6 ]; then
    echo "    # Low-RAM PC: keep max_jobs: 1. Optional caps for build tools (job environment only):"
    [ "$_pp" = yes ] || echo "    # env:"
    echo "    #   DOTNET_GCHeapHardLimitPercent: \"0x32\"     # .NET GC heap at most 50% of RAM (hexadecimal)"
    echo "    #   NODE_OPTIONS: \"--max-old-space-size=1024\"  # Node heap at most 1 GB"
    echo "    # For .NET jobs on a small box, add -m:1 to dotnet build / dotnet test commands (one MSBuild process)."
  fi
  return 0
}

render_steps() {
  [ -n "$S_STEPS" ] || return 0
  echo
  echo "Next steps (blockers first; nothing here is run for you):"
  _n=0
  while IFS="$SEP" read -r _i _ti _s _d _a _fx _blk; do
    [ -n "$_i" ] || continue
    _n=$((_n + 1)); _tag="should fix"; [ "$_blk" = 1 ] && _tag="MUST FIX"
    _who="no root needed"; [ "$_fx" = true ] && _who="needs root"
    printf '%2d. [%s] %s: %s\n' "$_n" "$_tag" "$_ti" "$_d"
    printf '    do (%s): %s\n' "$_who" "${_a:-see the detail above}"
  done <<EOF
$S_STEPS
EOF
}

render_human() {
  echo "lanq-makeready $TOOL_VERSION (read-only: nothing is installed, changed or sent anywhere)"
  echo "VERDICT: $S_TEXT"
  if [ "$QUIET" != 1 ]; then
    echo
    printf '%s' "$CHECKS" | while IFS="$SEP" read -r _i _ti _s _d _a _adm _fx; do
      printf '[%-4s] %s: %s\n' "$_s" "$_ti" "$_d"
      [ "$_s" = SKIP ] && [ "$_adm" = true ] && echo "       skipped (needs admin); re-run as root to fill this in"
    done
  fi
  echo
  echo "Verdicts"
  printf '  %-15s %s\n' worker_git "$V_GIT" worker_archive "$V_ARCHIVE" dotnet10 "$V_DOTNET" docker "$V_DOCKER" overnight "$V_OVERNIGHT"
  [ "$F_OS" = Darwin ] || echo "Suited for: $M_SUITED"
  if [ "$QUIET" != 1 ]; then
    render_steps
    echo
    echo "# Paste into machines.yml (confirm the placeholders):"
    render_snippet
    echo
    echo "VERDICT: $S_TEXT"
  fi
  return 0
}

json_esc() { printf '%s' "$1" | LC_ALL=C tr -c ' -~' '?' | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; } # ASCII only: control chars, ESC, CR and non-UTF-8 bytes become '?'

json_list() { printf '%s' "$1" | awk -F', ' 'NF { for (i = 1; i <= NF; i++) printf "%s\"%s\"", (i == 1 ? "" : ", "), $i }'; }

render_json() { # $1 = 1 when redacting
  _h=$F_HOST; _u=$F_USER
  if [ "$1" = 1 ]; then _h="<redacted>"; _u="<redacted>"; fi
  printf '{\n  "schema": "lanq-makeready/v1",\n  "tool": {"name": "lanq-makeready", "version": "%s"},\n' "$TOOL_VERSION"
  printf '  "generatedAt": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf '  "host": {"hostname": "%s", "user": "%s", "os": "%s", "arch": "%s"},\n' "$(json_esc "$_h")" "$(json_esc "$_u")" "$(json_esc "${F_OSNAME:-$F_OS}")" "$(json_esc "$F_ARCH")"
  printf '  "verdicts": {"worker_git": "%s", "worker_archive": "%s", "dotnet10": "%s", "docker": "%s", "overnight": "%s"},\n' "$V_GIT" "$V_ARCHIVE" "$V_DOTNET" "$V_DOCKER" "$V_OVERNIGHT"
  printf '  "summary": {"verdict": "%s", "text": "%s", "blockers": %s, "warnings": %s, "nextSteps": [' "$S_VERDICT" "$S_TEXT" "$S_BLOCKERS" "$S_WARNINGS"
  _n=0
  while IFS="$SEP" read -r _i _ti _s _d _a _fx _blk; do
    [ -n "$_i" ] || continue
    _n=$((_n + 1)); [ "$_n" = 1 ] || printf ','
    _b=false; [ "$_blk" = 1 ] && _b=true
    printf '\n    {"step": %s, "id": "%s", "title": "%s", "status": "%s", "blocker": %s, "detail": "%s", "action": "%s", "adminToFix": %s}' "$_n" "$_i" "$(json_esc "$_ti")" "$_s" "$_b" "$(json_esc "$_d")" "$(json_esc "$_a")" "${_fx:-false}"
  done <<EOF
$S_STEPS
EOF
  [ "$_n" = 0 ] || printf '\n  '
  printf ']},\n'
  _ct=false; [ "$M_DOCKER" = yes ] && _ct=true
  [ "$F_OS" = Darwin ] || printf '  "suitedFor": {"tier": "%s", "containers": %s, "text": "%s"},\n' "$M_TIER" "$_ct" "$(json_esc "$M_SUITED")"
  printf '  "checks": [\n'
  _first=1
  while IFS="$SEP" read -r _i _ti _s _d _a _adm _fx; do
    [ -z "$_i" ] && continue
    [ "$_first" = 1 ] || printf ',\n'
    _first=0
    printf '    {"id": "%s", "title": "%s", "status": "%s", "detail": "%s", "advice": "%s", "needsAdmin": %s}' "$_i" "$(json_esc "$_ti")" "$_s" "$(json_esc "$_d")" "$(json_esc "$_a")" "${_adm:-false}"
  done <<EOF
$CHECKS
EOF
  printf '\n  ],\n'
  printf '  "capabilities": [%s],\n' "$(json_list "$(printf '%s' "$M_CAPS" | sed -e 's/, linux$//' -e 's/^linux$//')")"
  printf '  "suggestedMachine": {"name": "%s", "os": "%s", "ssh": "%s", "work_root": "/srv/lq", "capabilities": [%s], "max_jobs": %s, "min_free_mem_gb": %s, "transfer": "%s"' "$(json_esc "$M_NAME")" "$M_OS" "$(json_esc "$M_SSH")" "$(json_list "$M_CAPS")" "$M_JOBS" "$M_MINMEM" "$M_TRANSFER"
  [ -n "$M_HOURS" ] && printf ', "allowed_hours": "%s"' "$M_HOURS"
  [ "$M_WAKE" = yes ] && printf ', "wake": {"mac": "%s"}' "$(json_esc "$F_WOL_MAC")"
  [ "$F_DOTNET_OFFPATH" = yes ] && [ "$F_DOTNET_OPEN" = no ] && printf ', "path_prepend": ["%s"], "env": {"DOTNET_ROOT": "%s"}' "$(json_esc "$F_DOTNET_DIR")" "$(json_esc "$F_DOTNET_DIR")"
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

sshd_conf_lines() { # $1 = file, $2 = depth: prints its lines in order with Include files expanded where they appear (as sshd reads them)
  [ -r "$1" ] || return 0
  while IFS= read -r _l || [ -n "$_l" ]; do
    case "$_l" in
      [Ii]nclude[[:space:]]*)
        [ "$2" -lt 3 ] || continue
        for _g in ${_l#*[[:space:]]}; do
          case "$_g" in /*) ;; *) _g=/etc/ssh/$_g ;; esac
          for _f in $_g; do printf '%s\n' "${SEP}include-start"; sshd_conf_lines "$_f" $(($2 + 1)); printf '%s\n' "${SEP}include-end"; done
        done ;;
      *) printf '%s\n' "$_l" ;;
    esac
  done < "$1"
}

gather_wol() { # physical wired NIC, its MAC, and ethtool Wake-on when readable without root
  F_WOL_IF=""; F_WOL_MAC=""; F_WOL_WIFI=no; F_WOL_SUP=""; F_WOL_ON=""
  _nl=""
  for _n in /sys/class/net/*; do
    [ -r "$_n/type" ] || continue
    _d=no; [ -e "$_n/device" ] && _d=yes
    _w=no; if [ -e "$_n/wireless" ] || [ -e "$_n/phy80211" ]; then _w=yes; [ "$_d" = yes ] && F_WOL_WIFI=yes; fi
    _nl="$_nl${_n##*/} $(cat "$_n/type" 2>/dev/null) $_d $_w $(cat "$_n/operstate" 2>/dev/null) $(cat "$_n/address" 2>/dev/null)
"
  done
  _pw=$(printf '%s' "$_nl" | pick_wired)
  [ -n "$_pw" ] || return 0
  F_WOL_IF=${_pw%% *}; F_WOL_MAC=${_pw#* }
  _et=""
  if have ethtool; then _et=ethtool; elif [ -x /usr/sbin/ethtool ]; then _et=/usr/sbin/ethtool; elif [ -x /sbin/ethtool ]; then _et=/sbin/ethtool; fi
  [ -n "$_et" ] || return 0
  _eo=$(run_t 5 "$_et" "$F_WOL_IF" 2>/dev/null)
  F_WOL_SUP=$(printf '%s\n' "$_eo" | sed -n 's/^[[:space:]]*Supports Wake-on:[[:space:]]*//p' | head -n 1)
  F_WOL_ON=$(printf '%s\n' "$_eo" | sed -n 's/^[[:space:]]*Wake-on:[[:space:]]*//p' | head -n 1)
}

sdk_root() { # $1 = a folder that may hold dotnet: adds its SDK majors to F_DOTNET; F_DOTNET_DIR = the folder of the newest stable SDK
  # as given (so /snap/dotnet-sdk/current stays valid across refreshes), _bd = that folder resolved; a tie keeps the earlier folder
  _d=${1%/}
  case "$_d" in /*) ;; *) return 0 ;; esac # absolute only (a relative one would depend on the working folder)
  [ "$(date +%s)" -le "$_end" ] && [ -x "$_d/dotnet" ] && [ -d "$_d/sdk" ] || return 0
  case "$_d" in *'"'*|*'\'*) return 0 ;; esac # a quote or backslash would break the YAML snippet
  [ "$(printf '%s' "$_d" | LC_ALL=C tr -d ' -~' | wc -c)" -eq 0 ] || return 0 # printable ASCII only: no newline, nothing json_esc turns into ?
  _r=$(readlink -f -- "$_d" 2>/dev/null) || _r=$_d
  case "$_seen" in *" $_r "*) return 0 ;; esac
  _seen="$_seen$_r "
  for _s in "$_r"/sdk/[0-9]*.[0-9]*.[0-9]*; do
    [ -f "$_s/dotnet.dll" ] || continue
    _v=${_s##*/}; _m=${_v%%.*}; _n=${_v#*.}; _mi=${_n%%.*}; _p=${_n#*.}; _p=${_p%%[!0-9]*}
    case "$_m.$_mi.$_p" in *[!0-9.]*|*..*|.*|*.|0[0-9]*|*.0[0-9]*|*[0-9][0-9][0-9][0-9][0-9][0-9]*) continue ;; esac # leading 0 = octal, long = overflow
    case " $F_DOTNET " in *" $_m "*) ;; *) F_DOTNET="${F_DOTNET:+$F_DOTNET }$_m" ;; esac
    case "$_v" in *-*) continue ;; esac # a preview counts for the capability, never as the newest
    _k=$(( $_m * 1000000 + $_mi * 1000 + $_p ))
    if [ "$_k" -gt "$_bk" ]; then _bk=$_k; F_DOTNET_DIR=$_d; _bd=$_r; _bn=$_v; fi
  done
  return 0
}

safe_dir() { # $1 = folder, $2 = its newest SDK: true when other local users cannot plant code a job's dotnet runs. The folder, its
  # dotnet, sdk and that SDK folder, host, host/fxr and its version folders, shared with every framework and version folder, packs, sdk-manifests
  # (each resolved): owned by root or this user, writable by nobody
  # else (a group may write when it is root's or this user's own private group). Every parent folder of the path as given and as
  # resolved: owned by root or this user, and not writable by others or another group unless sticky (then nobody can rename our entry).
  _u=$(id -u 2>/dev/null); _pg=0; [ "$(id -gn 2>/dev/null)" = "$(id -un 2>/dev/null)" ] && _pg=$(id -g 2>/dev/null)
  case "$1" in /*) ;; *) return 1 ;; esac
  for _x in "$1" "$1/dotnet" "$1/sdk" "$1/sdk/$2" "$1/host" "$1/host/fxr" "$1"/host/fxr/* "$1/shared" "$1"/shared/* "$1"/shared/*/* "$1/packs" "$1/sdk-manifests"; do
    case "$_x" in "$1/host"*|"$1/shared"*|"$1/packs"|"$1/sdk-manifests"|"$1/sdk"|"$1/sdk/") [ -e "$_x" ] && [ "$_x" != "$1/sdk/" ] || continue ;; esac
    _x=$(readlink -f -- "$_x" 2>/dev/null) || return 1
    [ -n "$(find "$_x" -maxdepth 0 \( -user 0 -o -user "$_u" \) ! -perm -0002 \( ! -perm -0020 -o -group 0 -o -group "$_pg" \) 2>/dev/null)" ] || return 1
  done
  _r=$(readlink -f -- "$1" 2>/dev/null) || return 1
  for _a in "$1" "$_r"; do
    while _n=${_a%/*}; [ "$_n" != "$_a" ] && [ -n "$_n" ]; do
      _a=$_n
      [ -n "$(find "$_a" -maxdepth 0 \( -user 0 -o -user "$_u" \) \( -type l -o -perm -1000 -o \( ! -perm -0002 \( ! -perm -0020 -o -group 0 -o -group "$_pg" \) \) \) 2>/dev/null)" ] || return 1
    done
  done
  return 0
}

in_list() { # $1 = a Match criterion, $2 = its comma list: true when it applies to the user (_ku) or one of their groups (_kg); ! negates
  case "$1" in [Uu]ser|[Gg]roup) ;; *) return 1 ;; esac
  _rl="$2,"; _hit=1
  while [ -n "$_rl" ]; do
    _pt=${_rl%%,*}; _rl=${_rl#*,}
    # shellcheck disable=SC2254,SC2086 # glob patterns as sshd reads them; group names are split on spaces
    case "$1:$_pt" in
      [Uu]ser:!*) case "$_ku" in ${_pt#!}) return 1 ;; esac ;;
      [Uu]ser:*) case "$_ku" in $_pt) _hit=0 ;; esac ;;
      [Gg]roup:!*) for _g in $_kg; do case "$_g" in ${_pt#!}) return 1 ;; esac; done ;;
      [Gg]roup:*) for _g in $_kg; do case "$_g" in $_pt) _hit=0 ;; esac; done ;;
    esac
  done
  return $_hit
}

keys_files() { # sshd config text on stdin, $1 = user, $2 = home, $3 = the user's groups -> line 1: default|global|match, then one
  # authorized-keys file per line. As sshd: the first AuthorizedKeysFile in a Match block that applies (all, or User/Group, at most
  # two criteria; anything else cannot be judged here, so that block is skipped) wins over the global one, else the default.
  _ku=$1; _kh=$2; _kg=" $3 "; _kv=""; _kw=default; _in=0; _ok=0; _gl=""; _gs=0; _hs=0; _stk=""
  while IFS= read -r _l || [ -n "$_l" ]; do
    case "$_l" in
      "${SEP}include-start") _stk="$_in$_ok $_stk"; continue ;;
      "${SEP}include-end") _t=${_stk%% *}; _stk=${_stk#* }; _in=${_t%?}; _ok=${_t#?}; continue ;;
    esac
    read -r _k _v <<EOF
${_l%%#*}
EOF
    case "$_k" in *=*) _v="${_k#*=} $_v"; _k=${_k%%=*} ;; esac
    _v=${_v#=}
    case "$(printf '%s' "$_k" | tr 'A-Z' 'a-z')" in
      match)
        _in=1; _ok=0
        read -r _c1 _p1 _c2 _p2 _more <<EOF
$_v
EOF
        if [ "$(printf '%s' "$_c1" | tr 'A-Z' 'a-z')" = all ] && [ -z "$_p1" ]; then _ok=1; continue; fi
        [ -z "$_more" ] && [ -n "$_p1" ] && in_list "$_c1" "$_p1" || continue
        if [ -n "$_c2" ]; then [ -n "$_p2" ] && in_list "$_c2" "$_p2" || continue; fi
        _ok=1 ;;
      authorizedkeysfile)
        if [ "$_in" = 0 ]; then if [ "$_gs" = 0 ]; then _gl=$_v; _gs=1; fi
        elif [ "$_ok" = 1 ] && [ "$_hs" = 0 ]; then _kv=$_v; _kw=match; _hs=1; fi ;;
    esac
  done
  if [ "$_hs" = 0 ] && [ "$_gs" = 1 ]; then _kv=$_gl; _kw=global; fi
  if [ "$_hs" = 0 ] && [ "$_gs" = 0 ]; then _kv=".ssh/authorized_keys .ssh/authorized_keys2"; fi
  echo "$_kw"
  _kid=$(id -u 2>/dev/null)
  printf '%s\n' "$_kv" | tr ' \t' '\n\n' | awk -v h="$_kh" -v u="$_ku" -v uid="$_kid" 'BEGIN { hh = h; gsub(/[\\&]/, "\\\\&", hh); uu = u; gsub(/[\\&]/, "\\\\&", uu) } { gsub(/"/, "") } $0 == "" || $0 == "none" { next } { gsub(/%%/, "\001"); gsub(/%h/, hh); gsub(/%u/, uu); gsub(/%U/, uid); gsub(/\001/, "%"); print (substr($0, 1, 1) == "/" ? $0 : h "/" $0) }'
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
    F_OSID=$(sed -n 's/^ID=//p' /etc/os-release 2>/dev/null | tr -d '"' | head -n 1)
    F_OSVERID=$(sed -n 's/^VERSION_ID=//p' /etc/os-release 2>/dev/null | tr -d '"' | head -n 1)
    F_NOW_YM=$(date +%Y%m)
    F_SWAPMB=$(awk '/^SwapTotal:/ {print int($2/1024)}' /proc/meminfo 2>/dev/null)
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
    F_SSHD_PW=unknown
    if [ "$F_SSHD" != notinstalled ]; then
      _sd=/usr/sbin/sshd; have sshd && _sd=sshd
      [ "$(id -u)" = 0 ] && F_SSHD_PW=$(run_t 5 "$_sd" -T 2>/dev/null | awk '$1 == "passwordauthentication" { print $2; exit }')
      if [ -z "$F_SSHD_PW" ] || [ "$F_SSHD_PW" = unknown ]; then
        F_SSHD_PW=unknown; [ -r /etc/ssh/sshd_config ] && F_SSHD_PW=$(sshd_conf_lines /etc/ssh/sshd_config 0 | sshd_pw_auth)
      fi
    fi
    _hdd=0; _ssd=0
    for _q in /sys/block/*/queue/rotational; do
      case "$_q" in */loop*|*/ram*|*/zram*|*/dm-*|*/sr*|*/nbd*) continue ;; esac
      [ -r "$_q" ] || continue
      if [ "$(cat "$_q")" = 1 ]; then _hdd=1; else _ssd=1; fi
    done
    if [ "$_ssd" = 1 ]; then F_DISK=SSD; elif [ "$_hdd" = 1 ]; then F_DISK=HDD; fi
    if have systemctl && { [ "$(run_t 5 systemctl is-enabled sleep.target 2>/dev/null)" = masked ] || [ "$(run_t 5 systemctl is-enabled suspend.target 2>/dev/null)" = masked ]; }; then F_SLEEP=masked
    elif have systemctl && [ -d /run/systemd/system ]; then
      F_SLEEP=unmasked
      _lap=no; for _x in /proc/acpi/button/lid/*; do [ -e "$_x" ] && _lap=yes; done
      _lv=$(for _x in /etc/systemd/logind.conf /usr/lib/systemd/logind.conf.d/*.conf /run/systemd/logind.conf.d/*.conf /etc/systemd/logind.conf.d/*.conf; do [ -r "$_x" ] && cat "$_x"; done | logind_values)
      # shellcheck disable=SC2086
      _ld=$(logind_auto_suspend $_lv "$_lap")
      _gt=""; _bus=no; _dm=no
      if have_session_bus; then _bus=yes; fi
      if [ -z "$_ld" ] && [ "$_bus" = yes ] && have gsettings; then _gt=$(run_t 5 gsettings get org.gnome.settings-daemon.plugins.power sleep-inactive-ac-type 2>/dev/null); fi
      if [ -z "$_ld" ] && [ -z "$_gt" ] && [ "$(run_t 5 systemctl is-active display-manager 2>/dev/null)" = active ]; then _dm=yes; fi
      _cs=$(classify_sleep "$_ld" "$_gt" "$_dm" "$_bus")
      F_SLEEP=${_cs%%|*}; F_SLEEP_DETAIL=${_cs#*|}
    fi
    [ -e /var/run/reboot-required ] && F_REBOOT=yes
    gather_wol
    if have systemctl && have_session_bus; then F_STARTUP=$(run_t 5 systemctl --user list-unit-files --state=enabled 2>/dev/null | grep -c '\.service'); fi
    if [ -e /sys/fs/cgroup/cgroup.controllers ]; then F_CGROUP=v2; elif [ -d /sys/fs/cgroup ]; then F_CGROUP=v1; fi
  fi
  F_SYSFREEGB=$(( $(df -Pk / 2>/dev/null | awk 'NR==2 {print $4}') / 1048576 ))
  _wd=/srv; [ -d /srv ] || _wd=/
  F_WORKFREEGB=$(( $(df -Pk "$_wd" 2>/dev/null | awk 'NR==2 {print $4}') / 1048576 ))
  _kc=/dev/null; F_AUTHVIA=""; if [ -r /etc/ssh/sshd_config ]; then _kc=/etc/ssh/sshd_config; else F_AUTHVIA=unreadable; fi
  _kf=$(sshd_conf_lines "$_kc" 0 | keys_files "$(id -un 2>/dev/null)" "$HOME" "$(id -Gn 2>/dev/null)")
  [ -n "$F_AUTHVIA" ] || F_AUTHVIA=$(printf '%s\n' "$_kf" | sed -n 1p)
  F_AUTHKEYS=no
  while IFS= read -r _f; do if [ -n "$_f" ] && [ -s "$_f" ]; then F_AUTHKEYS=yes; fi; done <<EOF
$(printf '%s\n' "$_kf" | sed 1d)
EOF
  F_GIT=""; if have git && mac_probe_ok git; then F_GIT=$(first_line "$(run_t 8 git --version 2>/dev/null)"); fi
  F_TARKIND=none; if have tar; then F_TARKIND=$(classify_tar "$(run_t 8 tar --version 2>&1)"); fi
  if have gzip; then F_GZIP=yes; else F_GZIP=no; fi
  # dotnet SDKs (no process is started): the folders lanq 0.8 searches, at most 5 s
  F_DOTNET=""; F_DOTNET_DIR=""; _bd=""; _bn=""; F_DOTNET_OFFPATH=no; F_DOTNET_OPEN=no; _dn=""; _seen=" "; _bk=0; _end=$(( $(date +%s) + 5 ))
  if have dotnet; then _dp=$(command -v dotnet)
    case "$_dp" in /snap/bin/*) _dn=/snap/dotnet-sdk/current ;; *) _dp=$(readlink -f -- "$_dp" 2>/dev/null || echo "$_dp"); _dn=${_dp%/*} ;; esac
  fi
  sdk_root "$_dn" # the dotnet on PATH first: it wins a tie
  for _d in "${DOTNET_ROOT:-}" "${DOTNET_ROOT_X64:-}" "${DOTNET_ROOT_ARM64:-}" "$HOME/.dotnet"; do sdk_root "$_d"; done
  for _f in /etc/dotnet/install_location /etc/dotnet/install_location_*; do [ -r "$_f" ] && sdk_root "$(sed -n 1p "$_f" | tr -d '\r')"; done
  for _d in /usr/share/dotnet /usr/lib/dotnet /usr/lib64/dotnet /opt/dotnet /snap/dotnet-sdk/current /usr/local/share/dotnet /opt/homebrew/opt/dotnet/libexec /usr/local/opt/dotnet/libexec; do sdk_root "$_d"; done
  if [ -n "$F_DOTNET_DIR" ] && [ "$_bd" != "$(readlink -f -- "$_dn" 2>/dev/null || echo "$_dn")" ]; then F_DOTNET_OFFPATH=yes
    safe_dir "$F_DOTNET_DIR" "$_bn" || F_DOTNET_OPEN=yes
  fi
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

# --out: the report goes to a new temp file (created with O_EXCL) in the same folder, then ln puts it in place.
# link(2) fails if the name exists as anything (file, FIFO, device, link), so nothing is ever overwritten or written through.
# Returns 0 written, 1 exists or not writable, 3 ln failed on a new name (no hard links on this filesystem).
save_out() {
  [ -e "$OUT_PATH" ] || [ -L "$OUT_PATH" ] && return 1
  case "$OUT_PATH" in */*) _odir=${OUT_PATH%/*}; [ -n "$_odir" ] || _odir=/ ;; *) _odir=. ;; esac
  case "$_odir" in /*) ;; *) _odir=./$_odir ;; esac # a folder named -x must not be read as an option
  _otmp=$(mktemp "$_odir/.lanq-makeready.XXXXXX" 2>/dev/null) || return 1
  trap 'rm -f -- "$_otmp"' EXIT
  trap 'rm -f -- "$_otmp"; exit 130' INT TERM
  printf '%s\n' "$1" > "$_otmp"
  ln -- "$_otmp" "$OUT_PATH" 2>/dev/null; _ok=$?
  rm -f -- "$_otmp"
  trap - EXIT INT TERM
  if [ "$_ok" != 0 ]; then { [ -e "$OUT_PATH" ] || [ -L "$OUT_PATH" ]; } && return 1; return 3; fi
  return 0
}

out_error() { # $1 = save_out's code
  if [ "$1" = 3 ]; then echo "lanq-makeready: could not create $OUT_PATH: this folder's filesystem may not support hard links (e.g. FAT/exFAT USB sticks); choose another folder. Nothing was written."
  else echo "lanq-makeready: could not create $OUT_PATH (it already exists now, or the folder is not writable); nothing was written."; fi
}

# ---------- main ----------

main() {
  JSON_PATH=""; OUT_PATH=""; REDACT=0; HOURS=""; QUIET=0
  while [ $# -gt 0 ]; do
    case "$1" in
      --json) [ $# -ge 2 ] || { echo "lanq-makeready: --json needs a path (or - for stdout)" >&2; exit 2; }; JSON_PATH=$2; shift ;;
      --out) [ $# -ge 2 ] && [ -n "$2" ] || { echo "lanq-makeready: --out needs a file name" >&2; exit 2; }; OUT_PATH=$2; shift ;;
      --redact) REDACT=1 ;;
      --hours) [ $# -ge 2 ] || { echo "lanq-makeready: --hours needs a value" >&2; exit 2; }; HOURS=$2; shift ;;
      --quiet) QUIET=1 ;;
      -h|--help) echo "usage: lanq-makeready.sh [--json PATH|-] [--out FILE] [--redact] [--hours HH:MM-HH:MM] [--quiet]"; exit 0 ;;
      *) echo "lanq-makeready: unknown argument: $1 (usage: [--json PATH|-] [--out FILE] [--redact] [--hours HH:MM-HH:MM] [--quiet])" >&2; exit 2 ;;
    esac
    shift
  done
  if [ -n "$HOURS" ] && ! valid_hours "$HOURS"; then
    echo "lanq-makeready: --hours must look like 00:00-07:00 (HH:MM-HH:MM, start different from end)." >&2
    exit 2
  fi
  if [ -n "$OUT_PATH" ] && { [ -e "$OUT_PATH" ] || [ -L "$OUT_PATH" ]; }; then
    echo "lanq-makeready: --out $OUT_PATH already exists; it is never overwritten. Choose a new file name." >&2
    exit 2
  fi
  gather_facts
  [ "$REDACT" = 1 ] && redact_facts
  eval_checks
  eval_verdicts
  eval_machine "$HOURS" "$REDACT"
  eval_summary
  _report=$(render_human)
  if [ "$JSON_PATH" = "-" ]; then render_json "$REDACT"
  else
    printf '%s\n' "$_report"
    [ -n "$JSON_PATH" ] && render_json "$REDACT" > "$JSON_PATH"
  fi
  # the only other write: a NEW file the user named
  if [ -n "$OUT_PATH" ]; then save_out "$_report" || out_error "$?" >&2; fi
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
