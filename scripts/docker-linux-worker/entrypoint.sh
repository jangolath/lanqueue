#!/bin/sh
# Entrypoint of the lanq Docker worker (see Dockerfile). setup.ps1 mounts, read-only:
#   /run/lanq/hostkey          the sshd host key (ed25519, private), generated on the PC so it survives rebuilds
#   /run/lanq/authorized_keys  the public key lanq logs in with (one line)
# Windows bind mounts show up as 0777 and owned by root, which sshd refuses (StrictModes), so both are copied into
# place with the right owner and modes. The key line gets the "restrict" option (no pty, no forwarding of any kind).
set -eu

src=/run/lanq
home=/home/lanq

if [ ! -s "$src/hostkey" ] || [ ! -s "$src/authorized_keys" ]; then
  echo "lanq worker: $src/hostkey and $src/authorized_keys must be mounted (run setup.ps1)" >&2
  exit 64
fi

install -d -m 0755 /run/sshd
install -m 0600 -o root -g root "$src/hostkey" /etc/ssh/ssh_host_ed25519_key

install -d -m 0700 -o lanq -g lanq "$home/.ssh"
tmp=$(mktemp)
# Every non-empty, non-comment line becomes one restricted key; a line that already has options is refused.
count=0
while IFS= read -r line || [ -n "$line" ]; do
  line=$(printf '%s' "$line" | tr -d '\r')
  case "$line" in
    '' | '#'*) continue ;;
    ssh-* | ecdsa-* | sk-*) printf 'restrict %s\n' "$line" >> "$tmp"; count=$((count + 1)) ;;
    *) echo "lanq worker: authorized_keys line with options or an unknown key type refused" >&2; rm -f "$tmp"; exit 65 ;;
  esac
done < "$src/authorized_keys"
if [ "$count" -eq 0 ]; then
  echo "lanq worker: no key in $src/authorized_keys" >&2
  rm -f "$tmp"
  exit 65
fi
install -m 0600 -o lanq -g lanq "$tmp" "$home/.ssh/authorized_keys"
rm -f "$tmp"

# The work and NuGet volumes start out owned by root when Docker creates them over a missing folder.
chown lanq:lanq /srv/lq "$home/.nuget"
chmod 0700 /srv/lq

/usr/sbin/sshd -t
exec /usr/sbin/sshd -D -e
