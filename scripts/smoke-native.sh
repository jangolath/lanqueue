#!/bin/sh
# Smoke-tests a built lanq binary with no network and no SSH: --version (with the embedded lanq-makeready scripts read back), loading
# the example config, and the one-JSON-object promise of --json. Runs on any POSIX sh (also Git Bash, and inside an old distro container).
# Usage: scripts/smoke-native.sh path/to/lanq <expected version, for example 0.5.0>
set -eu

bin=${1:-}
want=${2:-}
if [ -z "$bin" ] || [ -z "$want" ]; then
  echo "usage: scripts/smoke-native.sh <lanq binary> <expected version>" >&2
  exit 2
fi
root=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
trap 'rm -rf "$work"; exit 130' INT
trap 'rm -rf "$work"; exit 143' TERM
export LANQ_HOME="$work/home"

fail() {
  echo "smoke FAILED: $1" >&2
  exit 1
}

echo "== lanq --version"
"$bin" --version > "$work/version.txt" || fail "--version exited $?"
cat "$work/version.txt"
head -n 1 "$work/version.txt" | grep -q -E "^lanq $(printf '%s' "$want" | sed 's/[.]/[.]/g')([+].*)?\$" || fail "--version is not lanq $want"
grep -q -E '^lanq-makeready [0-9.]+ [(]embedded: lanq-makeready[.]ps1 [1-9][0-9]* bytes, lanq-makeready[.]sh [1-9][0-9]* bytes[)]' "$work/version.txt" \
  || fail "the embedded lanq-makeready scripts did not load"
grep -q -E '^lanq-makeready-tune [0-9.]+ [(]embedded: lanq-makeready-tune[.]ps1 [1-9][0-9]* bytes, lanq-makeready-tune[.]sh [1-9][0-9]* bytes[)]' "$work/version.txt" \
  || fail "the embedded lanq-makeready-tune scripts did not load"

echo "== lanq doctor --config machines.example.yml"
# Its exit code depends on this host (ssh, git, known_hosts); only the config line matters here.
"$bin" doctor --config "$root/machines.example.yml" > "$work/doctor.txt" 2>&1 || true
cat "$work/doctor.txt"
grep -q -E '^OK +config: 4 machine[(]s[)]' "$work/doctor.txt" || fail "the example config did not load"

echo "== lanq doctor with a wake: block (the static config context must know WakeConfig)"
printf 'policy:\n  allowed_commands: [dotnet test]\nmachines:\n  - name: sleeper\n    os: linux\n    ssh: build@sleeper.lan\n    work_root: /srv/lanq\n    wake:\n      mac: !!str "aa:bb:cc:dd:ee:01"\n      broadcast: 10.20.30.255\n      wait_seconds: 90\n' > "$work/wake.yml"
"$bin" doctor --config "$work/wake.yml" > "$work/wake.txt" 2>&1 || true
cat "$work/wake.txt"
grep -q -E '^OK +config: 1 machine[(]s[)]' "$work/wake.txt" || fail "a config with a wake: block did not load"
grep -q -F 'sleeper: wake: magic packet to 10.20.30.255:9 for aa:bb:cc:dd:ee:01, waits up to 90 s' "$work/wake.txt" || fail "the wake: settings were not read"

echo "== lanq health --json with an unknown key inside wake:"
printf 'policy:\n  allowed_commands: [dotnet test]\nmachines:\n  - name: sleeper\n    os: linux\n    ssh: build@sleeper.lan\n    work_root: /srv/lanq\n    wake:\n      mac: "aa:bb:cc:dd:ee:01"\n      colour: red\n' > "$work/wake-bad.yml"
code=0
"$bin" health --json --config "$work/wake-bad.yml" > "$work/wake-bad.json" 2> "$work/wake-bad.err" || code=$?
cat "$work/wake-bad.json"
[ "$code" = 2 ] || fail "health --json with a bad wake: exited $code, expected 2"
grep -q -E '^[{]"status":"Rejected","exitCode":2,"message":"Invalid config: .*colour' "$work/wake-bad.json" || fail "unexpected health --json object for a bad wake:"

echo "== lanq health --json with an invalid config"
printf 'machines: []\npolicy:\n  allowed_commands: [dotnet test]\n' > "$work/empty.yml"
code=0
"$bin" health --json --config "$work/empty.yml" > "$work/health.json" 2> "$work/health.err" || code=$?
cat "$work/health.json"
[ "$code" = 2 ] || fail "health --json exited $code, expected 2"
[ "$(wc -l < "$work/health.json" | tr -d ' ')" = 1 ] || fail "health --json did not print exactly one line"
grep -q -E '^[{]"status":"Rejected","exitCode":2,"message":"No machines configured[.]",' "$work/health.json" || fail "unexpected health --json object"
grep -q -E '"nextSteps":\[[{]"action":"[a-z_]+"' "$work/health.json" || fail "health --json error object has no nextSteps"

echo "== lanq run --json with a bad option"
code=0
"$bin" run --json --frobnicate > "$work/run.json" 2> "$work/run.err" || code=$?
cat "$work/run.json"
[ "$code" = 2 ] || fail "run --json exited $code, expected 2"
grep -q -E '^[{]"status":"Rejected","exitCode":2,"machine":null,"jobId":null,"durationSeconds":0,"message":"' "$work/run.json" || fail "unexpected run --json object"

echo "smoke OK: $bin"
