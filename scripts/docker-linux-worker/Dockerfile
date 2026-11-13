# A Linux lanq worker in a container: sshd, git and the .NET 10 SDK, key-only login as the user lanq, nothing else.
# Built and started by setup.ps1 (next to this file); see docs/WORKERS.md, "A Linux worker in Docker on your own PC".
#
# The base image is pinned by digest (a test checks it). To move to a newer SDK/runtime patch, resolve the digest again
# (docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0-noble), check its runtime with
# docker run --rm <image> dotnet --list-runtimes, update the line below and rebuild with setup.ps1 -Force.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb

# openssh-server: the only way in. procps: pgrep/ps (lanq-makeready and job scripts). tar/gzip: transfer: archive.
# The image keeps no host keys: the entrypoint installs the one setup.ps1 generated, so it survives rebuilds.
# The user lanq has no password ('*': no password login, key login still works) and no sudo.
RUN apt-get update \
 && apt-get install -y --no-install-recommends openssh-server git procps ca-certificates tar gzip \
 && rm -rf /var/lib/apt/lists/* \
 && rm -f /etc/ssh/ssh_host_* \
 && useradd --create-home --shell /bin/bash lanq \
 && usermod -p '*' lanq \
 && install -d -m 0700 -o lanq -g lanq /home/lanq/.ssh /home/lanq/.nuget /srv/lq

COPY 10-lanq.conf /etc/ssh/sshd_config.d/10-lanq.conf
COPY entrypoint.sh /usr/local/sbin/lanq-worker-entrypoint
RUN chmod 0644 /etc/ssh/sshd_config.d/10-lanq.conf \
 && chmod 0755 /usr/local/sbin/lanq-worker-entrypoint

EXPOSE 22
ENTRYPOINT ["/usr/local/sbin/lanq-worker-entrypoint"]
