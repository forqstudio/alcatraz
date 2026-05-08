#!/bin/sh
set -eu

CA_DIR="${CA_DIR:-/run/alcatraz-ca}"
CA_KEY="$CA_DIR/alcatraz_ca"
CA_PUB="$CA_DIR/alcatraz_ca.pub"
# UID/GID of the `app` user inside mcr.microsoft.com/dotnet/aspnet:8.0.
# OpenSSH refuses to use a private key with group/world-readable bits, so
# instead of relaxing the mode we transfer ownership to the API runtime user.
APP_UID="${APP_UID:-1654}"
APP_GID="${APP_GID:-1654}"

mkdir -p "$CA_DIR"

if [ -s "$CA_KEY" ] && [ -s "$CA_PUB" ]; then
    echo "ca-init: CA key already exists at $CA_KEY; reapplying ownership."
    chown "$APP_UID:$APP_GID" "$CA_KEY" "$CA_PUB"
    chmod 0600 "$CA_KEY"
    chmod 0644 "$CA_PUB"
    exit 0
fi

echo "ca-init: generating Ed25519 CA key at $CA_KEY"
ssh-keygen -t ed25519 -f "$CA_KEY" -N "" -C "alcatraz-demo-ca"
chown "$APP_UID:$APP_GID" "$CA_KEY" "$CA_PUB"
chmod 0600 "$CA_KEY"
chmod 0644 "$CA_PUB"
echo "ca-init: done (owner=$APP_UID:$APP_GID)."
