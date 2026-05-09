#!/usr/bin/env bash
set -euo pipefail
cd /workspaces/alcatraz

echo "==> dotnet restore (api)"
dotnet restore alcatraz.api/Alcatraz.sln

echo "==> dotnet restore (cli)"
dotnet restore alcatraz.cli/Alcatraz.Cli.sln

echo "==> go mod download (worker)"
(cd alcatraz.worker && go mod download)

echo "==> go mod download (routes)"
(cd alcatraz.routes && go mod download)

cat <<BANNER

Alcatraz devcontainer ready.
  dotnet : $(dotnet --version)
  go     : $(go version | awk '{print $3}')

Heavy / privileged steps still run on the host (need KVM, sudo, Docker):
  sudo -E ./alcatraz.core/build-kernel.sh        # ~10 min, one-time
  sudo -E ./alcatraz.core/build-rootfs.sh        # ~10 min, one-time
  docker compose up -d --build                   # control plane
  sudo alcatraz.worker/scripts/sync-ca-pubkey.sh # after each host reboot
  sudo -E ./alcatraz.worker/bin/alcatraz-worker  # worker (host-run)

See README.md "Quick start" for the full walkthrough.
BANNER
