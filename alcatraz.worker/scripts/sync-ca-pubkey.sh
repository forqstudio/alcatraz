#!/usr/bin/env bash
set -euo pipefail

# Copies the SSH CA pubkey out of the alcatraz_ca docker volume into
# /run/alcatraz-ca/, where the host-side worker reads it. /run is tmpfs;
# re-run this after every host reboot, before starting the worker.

DEST_DIR="/run/alcatraz-ca"
DEST_FILE="${DEST_DIR}/alcatraz_ca.pub"

if ! docker volume inspect alcatraz_ca >/dev/null 2>&1; then
    echo "alcatraz_ca docker volume not found. Run 'docker compose up -d' first." >&2
    exit 1
fi

sudo install -d -m 0755 "${DEST_DIR}"
docker run --rm -v alcatraz_ca:/ca alpine cat /ca/alcatraz_ca.pub \
    | sudo tee "${DEST_FILE}" > /dev/null
sudo chmod 0644 "${DEST_FILE}"

echo "Wrote $(wc -c < "${DEST_FILE}") bytes to ${DEST_FILE}"
