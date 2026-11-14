#!/bin/sh
# lanq-makeready-tune: READ-ONLY tuning report for a Linux LanQueue worker (macOS: no tune checks).
# Changes nothing, installs nothing, makes no network calls, needs no root (a few rows fill in as root).
# Tuning never affects readiness: there is never a FAIL, and the exit code is always 0 (2 = bad arguments).
# Fixes for some rows live in the separate fix script (lanq-makeready-fix.sh --tune), run as root at the PC itself.
#
# Usage: lanq-makeready-tune.sh [--json PATH|-] [--redact] [--hours HH:MM-HH:MM] [--work-root /abs/path] [--quiet]
#        --json - writes ONLY the JSON to stdout (over SSH: sh -s -- --json - --redact < this-file)

TOOL_VERSION=0.5.0
SEP=$(printf '\037')
NL=$(printf '\nx'); NL=${NL%x}
ITEMS=""
R=""          # root prefix for /proc, /sys, /etc, /var and /run reads; tests point it at a fake root
DEADLINE=90     # seconds for the whole scan; a probe that would start after it reports SKIP (time budget)
T_END=0

# BEGIN ADVICE
# The ONE table of advice text. Nothing here is ever executed by this script.
tadvice() {
  case "$1" in
    gov.powersave) echo "This CPU driver with the powersave governor keeps the CPU at its lowest clock, so builds run slowly. Find what set it first (tuned, TLP, power-profiles-daemon, a cpupower service), then switch to schedutil or ondemand there, or: sudo cpupower frequency-set -g schedutil" ;;
    gov.unknown) echo "This check does not know this frequency driver. If builds feel slow, look at its governor yourself (cpupower frequency-info)." ;;
    swap.high) echo "Report only. With little RAM a high swappiness swaps build processes out early. To try a lower value: sudo sysctl vm.swappiness=10 (until reboot); keep it with a file in /etc/sysctl.d." ;;
    inotify.low) echo "Few inotify instances: file watchers in dotnet, node and IDE tools fail with 'too many open files' or 'inotify limit reached'. Fix at the PC: sudo sh lanq-makeready-fix.sh --tune (sysctl.tune writes /etc/sysctl.d/99-zz-lanq-makeready.conf; undo puts the old values back)." ;;
    journal.big) echo "journald keeps up to 10% of its filesystem (at most 4 GB) by default. To cap it at the PC: sudo sh lanq-makeready-fix.sh --tune (journald.cap adds a drop-in; the trimmed logs cannot be restored)." ;;
    svc.fix) echo "Services a headless build box rarely needs. Fix at the PC: sudo sh lanq-makeready-fix.sh --tune (services.unused runs systemctl disable --now on cups, cups-browsed, bluetooth and ModemManager, each confirmed; never masks; undo re-enables). cups-browsed listens on the network (CVE-2024-47176): disable it if you do not print." ;;
    svc.avahi) echo "avahi-daemon is reported only: it answers for host.local names, and disabling it can make this worker unreachable by that name. Disable it yourself only if machines.yml uses an IP or a DNS name: sudo systemctl disable --now avahi-daemon.service avahi-daemon.socket" ;;
    reboot.window) echo "Report only. Automatic reboots inside the allowed hours can kill a running job. Move the reboot time out of the window (Unattended-Upgrade::Automatic-Reboot-Time in /etc/apt/apt.conf.d/50unattended-upgrades; dnf-automatic: reboot = never in /etc/dnf/automatic.conf) or reboot by hand." ;;
    reboot.nohours) echo "Report only. Run again with --hours HH:MM-HH:MM (the machine's allowed_hours) to check that the automatic reboot falls outside the build window." ;;
    pkgcache.big) echo "Report only; this tool never deletes anything. Downloaded package files can go: sudo apt clean (Debian/Ubuntu), sudo dnf clean packages (Fedora/RHEL), sudo apk cache clean (Alpine), sudo paccache -r (Arch)." ;;
    trim.off) echo "TRIM keeps an SSD fast. Fix at the PC: sudo sh lanq-makeready-fix.sh --tune (fstrim.timer enables the weekly timer; undo disables it again)." ;;
    trim.none) echo "No fstrim.timer unit here. Run sudo fstrim -av now and then, or install util-linux (it ships the timer)." ;;
    smart.fail) echo "Report only. A disk reports a failing SMART health check: back up and replace it before builds start failing. Details: sudo smartctl -a /dev/DISK" ;;
    thermal) echo "Report only. The CPU slowed itself down because it ran hot. Clean the fans and heatsink, check the airflow, and on an old PC consider new thermal paste. Also check the BIOS power and C-state settings." ;;
    disk.full) echo "Report only. A filesystem over 90% full slows builds and fails them when it fills. Clean old worktrees and build output, or the package cache; this tool never deletes anything." ;;
    fix.hint) echo "Possible fixes (run at the PC): sudo sh lanq-makeready-fix.sh --tune --dry-run" ;;
    *) echo "" ;;
  esac
}
# END ADVICE

# ---------- pure helpers (no I/O): used by tests ----------

add_item() { # id title status detail advice-key needs-admin-to-read fix-id(or empty) security(true|false)
  _adv=$(tadvice "$5")
  if [ "${REDACT:-0}" = 1 ]; then _det=$(rv "$4"); else _det=$4; fi   # advice is fixed text: never rewritten
  ITEMS="${ITEMS}$1${SEP}$2${SEP}$3${SEP}${_det}${SEP}${_adv}${SEP}${6:-false}${SEP}${7:-}${SEP}${8:-false}${NL}"
}
item_status() { printf '%s' "$ITEMS" | awk -F"$SEP" -v i="$1" '$1 == i { print $3 }'; }
item_field() { printf '%s' "$ITEMS" | awk -F"$SEP" -v i="$1" -v n="$2" '$1 == i { print $n }'; }

valid_hours() { # HH:MM-HH:MM, hours 00-23, start differs from end (the scanner's rule)
  case "$1" in
    [0-2][0-9]:[0-5][0-9]-[0-2][0-9]:[0-5][0-9]) ;;
    *) return 1 ;;
  esac
  _s=${1%-*}; _e=${1#*-}
  [ "$_s" = "$_e" ] && return 1
  [ "${_s%%:*}" -le 23 ] && [ "${_e%%:*}" -le 23 ]
}
valid_work_root() { # lanq's Linux work_root rule: absolute, [A-Za-z0-9_./-] only, no '..'
  case "$1" in /*) ;; *) return 1 ;; esac
  case "$1" in *[!A-Za-z0-9_./-]*|*..*) return 1 ;; esac
  [ "${#1}" -le 255 ]
}
mins() { printf '%s\n' "$1" | awk -F: '{ print $1 * 60 + $2 }'; }
in_hours() { # $1 HH:MM, $2 HH:MM-HH:MM: true when the time falls inside (start included, end not; wraps past midnight)
  _t=$(mins "$1"); _a=$(mins "${2%-*}"); _b=$(mins "${2#*-}")
  if [ "$_a" -lt "$_b" ]; then [ "$_t" -ge "$_a" ] && [ "$_t" -lt "$_b" ]
  else [ "$_t" -ge "$_a" ] || [ "$_t" -lt "$_b" ]; fi
}

# Redaction works on single values before anything is rendered (the scanner's rule).
rv() {
  _t=$1
  for _tok in "$R_HOME" "$R_USER" "$R_HOST"; do
    [ "${#_tok}" -ge 2 ] && [ "$_tok" != root ] || continue
    _e=$(printf '%s' "$_tok" | sed -e 's/[][\.*^$/|?+(){}]/\\&/g')
    _t=$(printf '%s\n' "$_t" | sed -E -e "s/(^|[^A-Za-z0-9_])$_e([^A-Za-z0-9_]|\$)/\\1<redacted>\\2/g" -e "s/(^|[^A-Za-z0-9_])$_e([^A-Za-z0-9_]|\$)/\\1<redacted>\\2/g")
  done
  printf '%s' "$_t"
}

gov_finding() { # $1 scaling_driver, $2 governors (space list) -> "STATUS|detail"
  case "$1" in
    intel_pstate|amd-pstate-epp) echo "OK|driver $1, governor $2 (with this driver powersave is the normal dynamic mode, not a clock cap)" ;;
    acpi-cpufreq|intel_cpufreq)
      case " $2 " in
        *" powersave "*) echo "WARN|driver $1 with governor powersave: the CPU stays at its lowest clock" ;;
        *" userspace "*) echo "INFO|driver $1 with governor userspace: a program sets the clock by hand" ;;
        *) echo "OK|driver $1, governor $2" ;;
      esac ;;
    *) echo "INFO|driver $1, governor $2 (a driver this check does not know)" ;;
  esac
}
# The fix script carries copies of inotify_finding, journald_limits, journal_finding and svc_offer, unchanged; a test
# checks that the copies match, so a fix is offered only for what this report shows.
inotify_finding() { # $1 max_user_instances, $2 max_user_watches -> OK | WARN | INFO
  if [ "$1" -le 128 ]; then echo WARN; elif [ "$2" -lt 524288 ]; then echo INFO; else echo OK; fi
}
journald_limits() { # journald.conf text (main file, then drop-ins) on stdin -> "SystemMaxUse RuntimeMaxUse", "-" when unset; the last value wins
  awk '/^[ \t]*[#;]/ { next } { gsub(/[ \t]/, "") } /^SystemMaxUse=/ { s = substr($0, 14) } /^RuntimeMaxUse=/ { r = substr($0, 15) } END { print (s == "" ? "-" : s) " " (r == "" ? "-" : r) }'
}
journal_finding() { # $1 persistent KB, $2 volatile (/run, RAM) KB, $3 SystemMaxUse, $4 RuntimeMaxUse -> INFO when worth a cap, else OK
  if [ "$3" = - ] && [ "$1" -ge 1048576 ]; then echo INFO; return 0; fi
  if [ "$4" = - ] && [ "$2" -ge 262144 ]; then echo INFO; return 0; fi
  echo OK
}
svc_offer() { # $1 service key, $2 desktop running (yes|no), $3 Bluetooth input device (yes|no) -> fix | report | none
  case "$1" in
    cups-browsed) echo fix ;;
    bluetooth) if [ "$2" = yes ] || [ "$3" = yes ]; then echo none; else echo fix; fi ;;
    cups|modemmanager) if [ "$2" = yes ]; then echo none; else echo fix; fi ;;
    avahi) if [ "$2" = yes ]; then echo none; else echo report; fi ;;
    *) echo none ;;
  esac
}
apt_values() { # apt.conf text on stdin -> "periodic reboot time" ("-" when unset); the last value wins
  awk '{ sub(/\/\/.*/, "") } /APT::Periodic::Unattended-Upgrade[ \t]+"/ { split($0, a, "\""); p = a[2] } /Unattended-Upgrade::Automatic-Reboot[ \t]+"/ { split($0, a, "\""); r = a[2] } /Unattended-Upgrade::Automatic-Reboot-Time[ \t]+"/ { split($0, a, "\""); t = a[2] } END { print (p == "" ? "-" : p) " " (r == "" ? "-" : r) " " (t == "" ? "-" : t) }'
}
dnf_values() { # dnf automatic.conf text on stdin -> "apply_updates reboot" ("-" when unset)
  awk '/^[ \t]*[#;]/ { next } { gsub(/[ \t]/, "") } /^apply_updates=/ { a = substr($0, 15) } /^reboot=/ { r = substr($0, 8) } END { print (a == "" ? "-" : a) " " (r == "" ? "-" : r) }'
}
reboot_finding() { # $1 reboot time HH:MM, $2 hours or empty, $3 what -> "STATUS|detail|advice-key"
  if [ -z "$2" ]; then echo "INFO|$3 reboots by itself at about $1|reboot.nohours"
  elif in_hours "$1" "$2"; then echo "WARN|$3 reboots by itself at about $1, inside the allowed hours $2|reboot.window"
  else echo "OK|$3 reboots by itself at about $1, outside the allowed hours $2|"; fi
}

# ---------- probes (read-only) ----------

have() { command -v "$1" >/dev/null 2>&1; }
run_t() { # run_t SECONDS cmd args... : bounded, stdin closed (the scanner's runner)
  _secs=$1; shift
  if have timeout; then timeout "$_secs" sh -c 'cd / || exit 1; exec "$@"' sh "$@" </dev/null; return $?; fi
  sh -c 'cd / || exit 1; exec "$@"' sh "$@" </dev/null &
  _p=$!
  ( sleep "$_secs"; kill "$_p" ) >/dev/null 2>&1 &
  _k=$!
  wait "$_p" 2>/dev/null
  _r=$?
  kill "$_k" >/dev/null 2>&1
  return "$_r"
}
now_s() { date +%s; }
due() { # $1 = seconds this probe may take: false when it would run past the deadline
  [ $(( $(now_s) + ${1:-1} )) -le "$T_END" ]
}
read1() { [ -r "$R$1" ] && head -n 1 "$R$1" 2>/dev/null | tr -d '\r'; }
is_num() { case "$1" in ''|*[!0-9]*) return 1 ;; *) return 0 ;; esac; }
dir_kb() { # bounded size of a folder in KB (0 when absent)
  [ -d "$1" ] || { echo 0; return 0; }
  _k=$(run_t 10 du -sk "$1" 2>/dev/null | awk 'NR == 1 { print $1 }')
  if is_num "$_k"; then echo "$_k"; else echo 0; fi
}
mb() { echo $(( $1 / 1024 )); }
have_systemd() { [ -d "$R/run/systemd/system" ] && have systemctl; }
unit_en() { _v=$(run_t 5 systemctl is-enabled "$1" 2>/dev/null | head -n 1); echo "${_v:-none}"; }
unit_act() { _v=$(run_t 5 systemctl is-active "$1" 2>/dev/null | head -n 1); echo "${_v:-unknown}"; }
in_container() { # docker, podman, LXC, systemd-nspawn
  [ -e "$R/.dockerenv" ] || [ -e "$R/run/.containerenv" ] || [ -s "$R/run/systemd/container" ] && return 0
  grep -qE '(docker|lxc|kubepods|containerd|libpod)' "$R/proc/1/cgroup" 2>/dev/null && return 0
  have systemd-detect-virt || return 1
  _v=$(run_t 5 systemd-detect-virt -c 2>/dev/null)
  case "$_v" in ''|none|wsl) return 1 ;; *) return 0 ;; esac   # WSL 2 counts as a host (its own kernel and services)
}
is_root() { [ "$(id -u 2>/dev/null)" = 0 ]; }
gui_session() { # a logind session of type x11 or wayland (a desktop in use, even without a display manager)
  have loginctl || return 1
  for _sid in $(run_t 5 loginctl list-sessions --no-legend 2>/dev/null | awk '{ print $1 }'); do
    case "$_sid" in ''|*[!A-Za-z0-9]*) continue ;; esac
    due 5 || return 1
    case "$(run_t 5 loginctl show-session "$_sid" -p Type 2>/dev/null)" in Type=x11|Type=wayland) return 0 ;; esac
  done
  return 1
}
desktop_running() { have_systemd && { [ "$(unit_act display-manager.service)" = active ] || gui_session; }; }
bt_input() { for _h in "$R"/sys/bus/hid/devices/0005:*; do [ -e "$_h" ] && return 0; done; return 1; } # a Bluetooth keyboard, mouse or pad
# A host-level check: SKIP inside a container (it would describe the host, which the container cannot change),
# and SKIP when the scan's time budget is spent.
host_check() { # id title seconds
  if [ "$CONTAINER" = true ]; then add_item "$1" "$2" SKIP "inside a container: this is a host setting; run the tune scan on the host" "" false "" false; return 1; fi
  due "$3" || { add_item "$1" "$2" SKIP "time budget: the scan stopped before this probe" "" false "" false; return 1; }
}

check_governor() {
  host_check cpu.governor "CPU frequency governor" 2 || return 0
  _drv=$(read1 /sys/devices/system/cpu/cpu0/cpufreq/scaling_driver)
  _gov=$(for _f in "$R"/sys/devices/system/cpu/cpu[0-9]*/cpufreq/scaling_governor; do [ -r "$_f" ] && head -n 1 "$_f"; done | sort -u | tr '\n' ' '); _gov=${_gov% }
  if [ -z "$_drv" ] || [ -z "$_gov" ]; then add_item cpu.governor "CPU frequency governor" SKIP "no cpufreq driver visible (common in virtual machines)" "" false "" false; return 0; fi
  _f=$(gov_finding "$_drv" "$_gov"); _s=${_f%%|*}; _k=""
  case "$_s" in WARN) _k=gov.powersave ;; INFO) _k=gov.unknown ;; esac
  add_item cpu.governor "CPU frequency governor" "$_s" "${_f#*|}" "$_k" false "" false
}

check_swappiness() {
  host_check sysctl.swappiness "vm.swappiness" 1 || return 0
  _v=$(read1 /proc/sys/vm/swappiness); _mem=$(awk '/^MemTotal:/ { print int($2 / 1024) }' "$R/proc/meminfo" 2>/dev/null)
  if ! is_num "$_v"; then add_item sysctl.swappiness "vm.swappiness" SKIP "not readable" "" false "" false; return 0; fi
  if [ "$_v" -gt 60 ] && is_num "$_mem" && [ "$_mem" -lt 8192 ]; then
    add_item sysctl.swappiness "vm.swappiness" INFO "$_v with $(( (_mem + 1023) / 1024 )) GB RAM (default 60)" swap.high false "" false
  else add_item sysctl.swappiness "vm.swappiness" OK "$_v" "" false "" false; fi
}

check_inotify() {
  host_check sysctl.inotify "inotify limits" 1 || return 0
  _i=$(read1 /proc/sys/fs/inotify/max_user_instances); _w=$(read1 /proc/sys/fs/inotify/max_user_watches)
  if ! is_num "$_i" || ! is_num "$_w"; then add_item sysctl.inotify "inotify limits" SKIP "not readable" "" false "" false; return 0; fi
  _s=$(inotify_finding "$_i" "$_w"); _d="max_user_instances $_i, max_user_watches $_w"
  case "$_s" in
    WARN) add_item sysctl.inotify "inotify limits" WARN "$_d (instances at or below 128; suggested 1024)" inotify.low false sysctl.tune false ;;
    INFO) add_item sysctl.inotify "inotify limits" INFO "$_d (watches below 524288)" inotify.low false sysctl.tune false ;;
    *) add_item sysctl.inotify "inotify limits" OK "$_d" "" false "" false ;;
  esac
}

check_journald() {
  host_check journald.size "systemd journal size" 25 || return 0
  if [ ! -d "$R/var/log/journal" ] && [ ! -d "$R/run/log/journal" ]; then add_item journald.size "systemd journal size" SKIP "no systemd journal here" "" false "" false; return 0; fi
  for _d in "$R"/var/log/journal/* "$R"/run/log/journal/*; do
    if [ -d "$_d" ] && { [ ! -r "$_d" ] || [ ! -x "$_d" ]; }; then add_item journald.size "systemd journal size" SKIP "the journal folders are not readable by this user" "" true "" false; return 0; fi
  done
  _p=$(dir_kb "$R/var/log/journal"); _r=$(dir_kb "$R/run/log/journal")
  _lim=$(for _x in "$R"/etc/systemd/journald.conf "$R"/usr/lib/systemd/journald.conf.d/*.conf "$R"/run/systemd/journald.conf.d/*.conf "$R"/etc/systemd/journald.conf.d/*.conf; do [ -r "$_x" ] && cat "$_x"; done | journald_limits)
  _sm=${_lim%% *}; _rm=${_lim#* }; _sc=$_sm; _rcap=$_rm
  [ "$_sc" = - ] && _sc="none set, so the default: 10% of the filesystem, at most 4 GB"
  [ "$_rcap" = - ] && _rcap="none set, so the default: 10% of /run, at most 4 GB"
  _d="persistent $(mb "$_p") MB (cap $_sc), volatile in /run (RAM) $(mb "$_r") MB (cap $_rcap)"
  if [ "$(journal_finding "$_p" "$_r" "$_sm" "$_rm")" = INFO ]; then add_item journald.size "systemd journal size" INFO "$_d" journal.big false journald.cap false
  else add_item journald.size "systemd journal size" OK "$_d" "" false "" false; fi
}

check_services() {
  host_check services.unused "Services a build box rarely needs" 5 || return 0
  if ! have_systemd; then add_item services.unused "Services a build box rarely needs" SKIP "systemd is not running here" "" false "" false; return 0; fi
  _dt=no; desktop_running && _dt=yes
  _bt=no; bt_input && _bt=yes
  _found=""; _st=OK; _fix=""; _sec=false; _key=""; _late=no
  for _e in cups:cups.service:cups.socket:cups.path cups-browsed:cups-browsed.service avahi:avahi-daemon.service:avahi-daemon.socket bluetooth:bluetooth.service modemmanager:ModemManager.service; do
    _svc=${_e%%:*}; _o=$(svc_offer "$_svc" "$_dt" "$_bt")
    [ "$_late" = yes ] && break
    [ "$_o" = none ] && continue
    _on=""
    for _u in $(printf '%s' "${_e#*:}" | tr ':' ' '); do
      due 5 || { _late=yes; break; }
      _en=$(unit_en "$_u"); _ac=$(unit_act "$_u")
      { [ "$_en" = enabled ] || [ "$_ac" = active ]; } && _on="$_on${_on:+, }$_u $_en/$_ac"
    done
    [ -n "$_on" ] || continue
    _found="$_found${_found:+; }$_on"
    [ "$_st" = OK ] && _st=INFO
    if [ "$_o" = fix ]; then _fix=services.unused; _key=svc.fix; fi
    [ "$_o" = report ] && [ -z "$_key" ] && _key=svc.avahi
    if [ "$_svc" = cups-browsed ]; then _st=WARN; _sec=true; fi
  done
  if [ "$_late" = yes ]; then add_item services.unused "Services a build box rarely needs" SKIP "time budget: the scan stopped during this probe" "" false "" false; return 0; fi
  _note=""; [ "$_dt" = yes ] && _note=" (a desktop is in use, so only cups-browsed is checked)"
  [ "$_dt" = no ] && [ "$_bt" = yes ] && _note=" (a Bluetooth input device is connected, so bluetooth is left out)"
  if [ "$_st" = OK ]; then add_item services.unused "Services a build box rarely needs" OK "none of cups, cups-browsed, avahi-daemon, bluetooth, ModemManager is enabled or running$_note" "" false "" false; return 0; fi
  _found="$_found$_note"
  case "$_found" in *avahi*) [ "$_key" = svc.fix ] && _found="$_found (avahi-daemon is report only: host.local names)" ;; esac
  add_item services.unused "Services a build box rarely needs" "$_st" "$_found" "$_key" false "$_fix" "$_sec"
}

check_unattended() {
  host_check unattended.reboot "Automatic update reboots" 5 || return 0
  _parts=""
  if [ -x "$R/usr/bin/unattended-upgrade" ] || [ -d "$R/etc/apt/apt.conf.d" ]; then
    _v=$(for _x in "$R"/etc/apt/apt.conf "$R"/etc/apt/apt.conf.d/*; do [ -f "$_x" ] && [ -r "$_x" ] && cat "$_x"; done | apt_values)
    set -- $_v
    if [ -x "$R/usr/bin/unattended-upgrade" ] && [ "$1" = 1 ] && [ "$2" = true ]; then
      _t=$3; case "$_t" in [0-2][0-9]:[0-5][0-9]) ;; *) _t=06:00 ;; esac   # "now" or unset: right after apt-daily-upgrade (06:00 plus up to 60 min)
      _parts="$(reboot_finding "$_t" "$HOURS" unattended-upgrades)"
    fi
  fi
  if [ -z "$_parts" ] && [ -r "$R/etc/dnf/automatic.conf" ]; then
    _v=$(dnf_values < "$R/etc/dnf/automatic.conf"); set -- $_v
    _timer=no
    _inst=no
    if have_systemd; then for _u in dnf-automatic.timer dnf-automatic-install.timer dnf5-automatic.timer; do
      due 5 || { add_item unattended.reboot "Automatic update reboots" SKIP "time budget: the scan stopped during this probe" "" false "" false; return 0; }
      [ "$(unit_en "$_u")" = enabled ] || continue
      _timer=yes; [ "$_u" = dnf-automatic-install.timer ] && _inst=yes
    done; fi
    case "$2" in when-changed|when-needed)
      if [ "$_timer" = yes ] && { [ "$1" = yes ] || [ "$_inst" = yes ]; }; then _parts=$(reboot_finding 06:00 "$HOURS" dnf-automatic); fi ;;
    esac
  fi
  if [ -z "$_parts" ]; then add_item unattended.reboot "Automatic update reboots" OK "no automatic reboot after updates is set up (unattended-upgrades, dnf-automatic)" "" false "" false; return 0; fi
  _s=${_parts%%|*}; _r=${_parts#*|}
  add_item unattended.reboot "Automatic update reboots" "$_s" "${_r%%|*}" "${_r#*|}" false "" false
}

check_pkgcache() {
  due 1 || { add_item pkgcache "Package cache" SKIP "time budget: the scan stopped before this probe" "" false "" false; return 0; }
  _t=0; _w=""
  for _d in var/cache/apt/archives var/cache/dnf var/cache/yum var/cache/apk var/cache/pacman/pkg var/cache/zypp/packages; do
    [ -d "$R/$_d" ] || continue
    due 10 || { add_item pkgcache "Package cache" SKIP "time budget: the scan stopped during this probe" "" false "" false; return 0; }
    _k=$(dir_kb "$R/$_d"); _t=$((_t + _k)); _w="$_w${_w:+, }/$_d $(mb "$_k") MB"
  done
  if [ -z "$_w" ]; then add_item pkgcache "Package cache" OK "no package cache folder found" "" false "" false
  elif [ "$_t" -gt 2097152 ]; then add_item pkgcache "Package cache" INFO "$(mb "$_t") MB: $_w" pkgcache.big false "" false
  else add_item pkgcache "Package cache" OK "$(mb "$_t") MB: $_w" "" false "" false; fi
}

trim_disks() { # non-rotational disks that accept discard (TRIM)
  for _q in "$R"/sys/block/*/queue/rotational; do
    [ -r "$_q" ] || continue
    _n=${_q%/queue/rotational}; _n=${_n##*/}
    case "$_n" in loop*|ram*|zram*|dm-*|sr*|nbd*|md*|fd*) continue ;; esac
    [ "$(head -n 1 "$_q")" = 0 ] || continue
    _dm=$(head -n 1 "${_q%/rotational}/discard_max_bytes" 2>/dev/null)
    is_num "$_dm" && [ "$_dm" -gt 0 ] && printf '%s ' "$_n"
  done
}
check_fstrim() {
  host_check fstrim "SSD TRIM" 6 || return 0
  _ssd=$(trim_disks); _ssd=${_ssd% }
  if [ -z "$_ssd" ]; then add_item fstrim "SSD TRIM" OK "no SSD that accepts TRIM found" "" false "" false; return 0; fi
  if awk '$2 == "/" { n = split($4, o, ","); for (i = 1; i <= n; i++) if (o[i] == "discard") f = 1 } END { exit !f }' "$R/proc/mounts" 2>/dev/null; then add_item fstrim "SSD TRIM" OK "$_ssd: / is mounted with discard (continuous TRIM)" "" false "" false; return 0; fi
  if ! have_systemd; then add_item fstrim "SSD TRIM" SKIP "$_ssd: systemd is not running here, so the timer cannot be checked" "" false "" false; return 0; fi
  case "$(unit_en fstrim.timer)" in
    enabled) add_item fstrim "SSD TRIM" OK "$_ssd: fstrim.timer is enabled" "" false "" false ;;
    disabled) add_item fstrim "SSD TRIM" INFO "$_ssd: fstrim.timer is disabled" trim.off false fstrim.timer false ;;
    none|not-found) add_item fstrim "SSD TRIM" INFO "$_ssd: no fstrim.timer unit" trim.none false "" false ;;
    *) add_item fstrim "SSD TRIM" INFO "$_ssd: fstrim.timer is $(unit_en fstrim.timer)" trim.none false "" false ;;
  esac
}

check_smart() {
  host_check smart "Disk health (SMART)" 12 || return 0
  if ! have smartctl; then add_item smart "Disk health (SMART)" SKIP "smartctl is not installed (smartmontools)" "" false "" false; return 0; fi
  if ! is_root; then add_item smart "Disk health (SMART)" SKIP "needs root" "" true "" false; return 0; fi
  _ok=""; _bad=""; _unk=""; _n=0
  for _b in "$R"/sys/block/*; do
    _d=${_b##*/}
    case "$_d" in sd[a-z]|sd[a-z][a-z]|nvme[0-9]n[0-9]|nvme[0-9][0-9]n[0-9]|hd[a-z]) ;; *) continue ;; esac
    _n=$((_n + 1)); [ "$_n" -le 8 ] || break
    if ! due 12; then _unk="$_unk${_unk:+, }$_d (time budget)"; continue; fi
    _o=$(run_t 10 smartctl -H -n standby "/dev/$_d" 2>/dev/null)
    case "$_o" in
      *FAILED*|*FAILING*) _bad="$_bad${_bad:+, }$_d" ;;
      *PASSED*|*": OK"*) _ok="$_ok${_ok:+, }$_d" ;;
      *) _unk="$_unk${_unk:+, }$_d" ;;
    esac
  done
  _d="passed: ${_ok:-none}${_unk:+; not read (standby, USB bridge or no SMART): $_unk}"
  if [ -n "$_bad" ]; then add_item smart "Disk health (SMART)" WARN "FAILING: $_bad; $_d" smart.fail false "" false
  elif [ -z "$_ok$_unk" ]; then add_item smart "Disk health (SMART)" SKIP "no disk SMART can read" "" false "" false
  else add_item smart "Disk health (SMART)" OK "$_d" "" false "" false; fi
}

check_thermal() {
  host_check thermal.throttle "CPU thermal throttling" 2 || return 0
  _c=0; _p=0; _any=no
  for _f in "$R"/sys/devices/system/cpu/cpu[0-9]*/thermal_throttle/core_throttle_count; do
    [ -r "$_f" ] || continue; _any=yes
    _v=$(head -n 1 "$_f"); is_num "$_v" && _c=$((_c + _v))
    _v=$(head -n 1 "${_f%/*}/package_throttle_count" 2>/dev/null); is_num "$_v" && [ "$_v" -gt "$_p" ] && _p=$_v
  done
  if [ "$_any" = no ]; then add_item thermal.throttle "CPU thermal throttling" SKIP "no thermal_throttle counters (not an Intel CPU, or a VM)" "" false "" false
  elif [ $((_c + _p)) -gt 0 ]; then add_item thermal.throttle "CPU thermal throttling" INFO "since boot: $_c core events (all cores), $_p package events" thermal false "" false
  else add_item thermal.throttle "CPU thermal throttling" OK "no throttle events since boot" "" false "" false; fi
}

df_used() { # $1 path -> "used% mountpoint" of its filesystem
  run_t 5 df -Pk "$1" 2>/dev/null | awk 'NR == 2 { sub(/%/, "", $5); print $5 " " $6 }'
}
check_disk() {
  due 6 || { add_item disk.full "Disk space used" SKIP "time budget: the scan stopped before this probe" "" false "" false; return 0; }
  _paths=/; if [ -n "$WORK_ROOT" ]; then _w=$WORK_ROOT; while [ ! -e "$_w" ] && [ "$_w" != / ]; do _w=${_w%/*}; [ -n "$_w" ] || _w=/; done; _paths="/ $_w"; fi
  _full=""; _all=""; _seen=" "
  for _p in $_paths; do
    _u=$(df_used "$_p"); [ -n "$_u" ] || continue
    _pc=${_u%% *}; _mp=${_u#* }
    case "$_seen" in *" $_mp "*) continue ;; esac
    _seen="$_seen$_mp "
    is_num "$_pc" || continue
    _all="$_all${_all:+, }$_mp $_pc%"
    [ "$_pc" -gt 90 ] && _full="$_full${_full:+, }$_mp $_pc%"
  done
  if [ -z "$_all" ]; then add_item disk.full "Disk space used" SKIP "df gave no answer" "" false "" false
  elif [ -n "$_full" ]; then add_item disk.full "Disk space used" WARN "over 90% used: $_full" disk.full false "" false
  else add_item disk.full "Disk space used" OK "$_all used" "" false "" false; fi
}

gather() {
  F_OS=$(uname -s 2>/dev/null); F_ARCH=$(uname -m 2>/dev/null); F_HOST=$(hostname 2>/dev/null || uname -n); F_USER=$(id -un 2>/dev/null)
  F_OSNAME=$(sed -n 's/^PRETTY_NAME=//p' "$R/etc/os-release" 2>/dev/null | tr -d '"' | head -n 1)
  CONTAINER=false; ELEVATED=false
  if [ "$F_OS" = Darwin ]; then F_OSNAME="macOS $(sw_vers -productVersion 2>/dev/null)"; return 0; fi
  in_container && CONTAINER=true
  is_root && ELEVATED=true
  T_END=$(( $(now_s) + DEADLINE ))
  check_governor; check_swappiness; check_inotify; check_journald; check_services
  check_unattended; check_pkgcache; check_fstrim; check_smart; check_thermal; check_disk
}

# ---------- output ----------

count_status() { printf '%s' "$ITEMS" | awk -F"$SEP" -v s="$1" '$3 == s { n++ } END { print n + 0 }'; }

render_human() {
  echo "lanq-makeready-tune $TOOL_VERSION (read-only: reports only, nothing is changed; tuning never affects readiness)"
  if [ "$F_OS" = Darwin ]; then echo "lanq-makeready-tune: no tune checks for macOS"; return 0; fi
  [ "$CONTAINER" = true ] && echo "Inside a container: host-level checks are skipped."
  echo
  printf '%s' "$ITEMS" | while IFS="$SEP" read -r _i _ti _s _d _a _adm _fid _sec; do
    [ -n "$_i" ] || continue
    [ "$QUIET" = 1 ] && [ "$_s" = OK ] && continue
    printf '[%-4s] %s: %s\n' "$_s" "$_ti" "$_d"
    [ "$_sec" = true ] && echo "       (security)"
    [ "$_s" = SKIP ] && [ "$_adm" = true ] && echo "       skipped (needs admin); re-run as root to fill this in"
    case "$_s" in WARN|INFO) [ -n "$_a" ] && echo "       $_a" ;; esac
  done
  echo
  echo "Summary: $(count_status WARN) WARN, $(count_status INFO) INFO (the readiness scan is lanq-makeready.sh; reboot.pending is reported there)"
  [ -n "$(printf '%s' "$ITEMS" | awk -F"$SEP" '$7 != "" { print $7 }')" ] && tadvice fix.hint
  return 0
}

json_esc() { printf '%s' "$1" | LC_ALL=C tr -c ' -~' '?' | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; } # the scanner's: ASCII only

render_json() {
  printf '{\n  "schema": "lanq-makeready-tune/v1",\n  "tool": {"name": "lanq-makeready-tune", "version": "%s"},\n' "$TOOL_VERSION"
  printf '  "generatedAt": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf '  "host": {'
  if [ "$REDACT" = 1 ]; then printf '"hostname": "<redacted>", "user": "<redacted>", '; else printf '"hostname": "%s", "user": "%s", ' "$(json_esc "$F_HOST")" "$(json_esc "$F_USER")"; fi
  _os=${F_OSNAME:-$F_OS}; [ "$REDACT" = 1 ] && _os=$(rv "$_os")
  _os=$(printf '%s' "$_os" | cut -c1-128)
  printf '"os": "%s", "arch": "%s"},\n' "$(json_esc "$_os")" "$(json_esc "$F_ARCH")"
  printf '  "context": {"container": %s, "elevated": %s' "${CONTAINER:-false}" "${ELEVATED:-false}"
  if [ -n "$WORK_ROOT" ]; then _wr=$WORK_ROOT; [ "$REDACT" = 1 ] && _wr=$(rv "$_wr"); printf ', "workRoot": "%s"' "$(json_esc "$_wr")"; fi
  [ -z "$HOURS" ] || printf ', "hours": "%s"' "$(json_esc "$HOURS")"
  printf '},\n  "items": ['
  _n=0
  printf '%s' "$ITEMS" | while IFS="$SEP" read -r _i _ti _s _d _a _adm _fid _sec; do
    [ -n "$_i" ] || continue
    _n=$((_n + 1)); [ "$_n" = 1 ] || printf ','
    _f=null; [ -n "$_fid" ] && _f="\"$_fid\""
    printf '\n    {"id": "%s", "title": "%s", "status": "%s", "detail": "%s", "advice": "%s", "needsAdmin": %s, "fixId": %s, "security": %s}' "$_i" "$(json_esc "$_ti")" "$_s" "$(json_esc "$_d")" "$(json_esc "$_a")" "${_adm:-false}" "$_f" "${_sec:-false}"
  done
  [ -z "$ITEMS" ] || printf '\n  '
  printf '],\n  "summary": {"ok": %s, "info": %s, "warn": %s, "skip": %s}\n}\n' "$(count_status OK)" "$(count_status INFO)" "$(count_status WARN)" "$(count_status SKIP)"
}

# ---------- main ----------

USAGE="usage: lanq-makeready-tune.sh [--json PATH|-] [--redact] [--hours HH:MM-HH:MM] [--work-root /abs/path] [--quiet]"
main() {
  JSON_PATH=""; REDACT=0; HOURS=""; WORK_ROOT=""; QUIET=0
  while [ $# -gt 0 ]; do
    case "$1" in
      --json) [ $# -ge 2 ] && [ -n "$2" ] || { echo "lanq-makeready-tune: --json needs a path (or - for stdout)" >&2; return 2; }; JSON_PATH=$2; shift ;;
      --redact) REDACT=1 ;;
      --hours) [ $# -ge 2 ] || { echo "lanq-makeready-tune: --hours needs a value" >&2; return 2; }; HOURS=$2; shift ;;
      --work-root) [ $# -ge 2 ] || { echo "lanq-makeready-tune: --work-root needs a path" >&2; return 2; }; WORK_ROOT=$2; shift ;;
      --quiet) QUIET=1 ;;
      -h|--help) echo "$USAGE"; return 0 ;;
      *) echo "lanq-makeready-tune: unknown argument: $1 ($USAGE)" >&2; return 2 ;;
    esac
    shift
  done
  if [ -n "$HOURS" ] && ! valid_hours "$HOURS"; then echo "lanq-makeready-tune: --hours must look like 00:00-07:00 (HH:MM-HH:MM, start different from end)." >&2; return 2; fi
  if [ -n "$WORK_ROOT" ] && ! valid_work_root "$WORK_ROOT"; then echo "lanq-makeready-tune: --work-root must be an absolute path like /srv/lq (letters, digits, _ . / - only; no '..')." >&2; return 2; fi
  R_HOME=${HOME:-}; R_USER=$(id -un 2>/dev/null); R_HOST=$(hostname 2>/dev/null || uname -n)
  gather
  if [ "$JSON_PATH" = "-" ]; then render_json
  else
    render_human
    [ -n "$JSON_PATH" ] && render_json > "$JSON_PATH"
  fi
  return 0
}

# LANQ_MAKEREADY_TUNE_LIB=1 is for tests that source this file AND define __lanq_makeready_tune_test_harness first.
# shellcheck disable=SC2317
if [ "${LANQ_MAKEREADY_TUNE_LIB:-}" = 1 ]; then
  if type __lanq_makeready_tune_test_harness >/dev/null 2>&1; then return 0 2>/dev/null || exit 0; fi
  echo "lanq-makeready-tune: LANQ_MAKEREADY_TUNE_LIB=1 is only for the test runner (it must source this file); unset it." >&2
  exit 2
fi
# stdin is closed for everything main runs, so nothing can consume the script when it arrives via 'sh -s'
main "$@" </dev/null
_rc=$?
[ "$_rc" = 2 ] && exit 2
exit 0
