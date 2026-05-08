#!/bin/sh
set -eu

# The CA pubkey is mounted from the alcatraz-ca shared volume.
CA_PUB_SRC="${CA_PUB_PATH:-/run/alcatraz-ca/alcatraz_ca.pub}"

if [ ! -s "$CA_PUB_SRC" ]; then
    echo "demo-sshd: waiting for CA pubkey at $CA_PUB_SRC..."
    for i in $(seq 1 30); do
        [ -s "$CA_PUB_SRC" ] && break
        sleep 1
    done
fi

if [ ! -s "$CA_PUB_SRC" ]; then
    echo "demo-sshd: CA pubkey never appeared at $CA_PUB_SRC; exiting." >&2
    exit 1
fi

install -m 0644 -o root -g root "$CA_PUB_SRC" /etc/ssh/alcatraz_ca.pub

# Start with an empty KRL — sshd accepts an empty file as 'no revoked keys'.
: > /etc/ssh/alcatraz_krl
chmod 0644 /etc/ssh/alcatraz_krl

# auth_principals/al is rewritten on every sandbox provision in production;
# in the demo, the operator pokes it with `docker exec` between sandbox creates.
[ -f /etc/ssh/auth_principals/al ] || : > /etc/ssh/auth_principals/al
chmod 0644 /etc/ssh/auth_principals/al

echo "demo-sshd: CA pubkey installed; starting sshd."
exec /usr/sbin/sshd -D -e
