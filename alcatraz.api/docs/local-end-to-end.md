# Local End-to-End — API wire protocol reference (curl)

> **The canonical end-to-end demo runs through `alcatraz.cli` — see [`../../alcatraz.cli/docs/local-end-to-end.md`](../../alcatraz.cli/docs/local-end-to-end.md).**
> This document keeps a `curl`-and-stock-`ssh` walkthrough of the same flow for anyone who needs to study the wire contract (device flow, register reconciliation, cert response shape) without going through the CLI.

It walks through the entire customer flow against a local `docker compose` stack — **device-flow login → create sandbox → worker boots VM → fetch SSH cert → SSH into the sandbox** — using `curl` and stock `ssh` so each HTTP request is verifiable on its own.

For an even simpler smoke test that doesn't need a worker, the `--profile demo-sshd` Alpine container still exists. See "Cert pipeline only (no worker)" at the bottom.

## Prerequisites

- Docker Compose
- `curl`, `jq`, `ssh-keygen`, `ssh` on the host
- A web browser (one click during device-flow login)
- KVM, CNI plugins, and a built kernel + rootfs in `alcatraz.core/` for the worker — see [`../../alcatraz.worker/README.md`](../../alcatraz.worker/README.md)

## What `docker compose up` brings up

| Service | Purpose |
|---|---|
| `alcatraz.api` | Our API on `:8080`. Subscribes to `vm.ready` (→ `Running`) and `vm.destroyed` (→ `Deleted` if we asked, otherwise `Failed`) |
| `alcatraz-db` | Postgres on `:5432` |
| `alcatraz-idp` | Keycloak on `:8082` (realm export pre-configured with device flow enabled) |
| `alcatraz-redis` | Redis on `:6379` |
| `alcatraz-nats` | NATS broker on `:4222` (monitoring on `:8222`) |
| `alcatraz-seq` | Seq log viewer on `:8083` |
| `alcatraz-ca-init` | Generates the shared CA key into the `alcatraz_ca` volume on first boot |

Two extra profiles:

- `--profile gateway` — Traefik (`network_mode: host`, ACME on `:443`) plus `alcatraz.routes`. For public-internet ingress.
- `--profile demo-sshd` — legacy Alpine `sshd` stand-in for the cert-pipeline-only path described at the bottom.

## First-time bring-up

The compose file lives at the repo root.

If you've previously run `docker compose up`, you must wipe volumes when the realm export changes (Keycloak only imports on first boot):

```bash
cd /path/to/alcatraz   # repo root
docker compose down -v
docker compose up -d --build
docker compose logs -f alcatraz.api
# Wait for "Now listening on: http://[::]:8080"
```

## Bring up the worker

The worker is host-run (it needs KVM and CNI). Copy the API's CA pubkey out of the shared compose volume so the worker can plant it into each VM's overlay. `/run` is tmpfs, so re-run this after every host reboot — the worker fails fast at startup if the file is missing.

```bash
alcatraz.worker/scripts/sync-ca-pubkey.sh
```

Then build and run:

```bash
cd alcatraz.worker
make build
sudo -E ./bin/alcatraz-worker
```

The worker subscribes to `vm.spawn` and starts publishing `vm.ready` once each VM's `sshd` is reachable.

## End-to-end walkthrough

### 1. Register a Keycloak user (one-time per stack)

The realm ships empty of human users. Registering through the API creates a Keycloak account *and* the matching local `users` row in one transaction; without that, claims transformation can't resolve the local `UserId`.

```bash
curl -sX POST http://localhost:8080/api/v1/users/register \
  -H 'content-type: application/json' \
  -d '{"email":"demo@alcatraz.local","firstName":"Demo","lastName":"User","password":"demopass"}'
```

Register is idempotent — re-running it for an existing email returns the existing user id rather than 500ing on Keycloak's 409. If Keycloak has the user but the local DB row is missing (e.g. you wiped the DB volume but not Keycloak's), register reconciles by creating the local row against the existing Keycloak identity.

### 2. Get an access token

**Fast path** (password grant, no browser, useful for scripting):

```bash
TOKEN=$(curl -sX POST http://localhost:8080/api/v1/users/login \
  -H 'content-type: application/json' \
  -d '{"email":"demo@alcatraz.local","password":"demopass"}' | jq -r .accessToken)
```

**Real device-flow path** (what the CLI does):

```bash
INIT=$(curl -sX POST http://localhost:8080/api/v1/auth/device)
DEVICE_CODE=$(echo "$INIT" | jq -r .deviceCode)
echo "Open: $(echo "$INIT" | jq -r .verificationUriComplete)"
read -p "Press Enter once you've signed in..."

TOKEN=$(curl -sX POST http://localhost:8080/api/v1/auth/device/token \
  -H 'content-type: application/json' \
  -d "{\"deviceCode\":\"$DEVICE_CODE\"}" | jq -r .accessToken)
```

While polling, `POST /auth/device/token` returns HTTP 400 with `"error": "authorization_pending"` until the browser sign-in completes — that's the RFC 8628 idiom the CLI implements.

### 3. Create a sandbox

```bash
SANDBOX=$(curl -sX POST http://localhost:8080/api/v1/sandboxes \
  -H "authorization: bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"vcpus":2,"memoryMib":2048}')
ID=$(echo "$SANDBOX" | jq -r .id)
echo "Sandbox $ID requested"
```

The API persists the row in `Provisioning`, writes a `SandboxRequested` outbox message in the same transaction, and the Quartz outbox drainer publishes `vm.spawn` on NATS.

### 4. Wait for the worker

The worker claims the `vm.spawn`, plants `auth_principals/al` and `trusted_user_ca_keys` into the AgentFS overlay, boots Firecracker, probes `vm_ip:22` until sshd accepts, and publishes `vm.ready`. The API's hosted `VmReadyConsumer` consumes that and marks the sandbox `Running`.

You can watch the events on NATS in another shell:

```bash
docker run --rm --network alcatraz_default natsio/nats-box:latest \
  nats sub 'vm.>' -s nats://alcatraz-nats:4222
```

Or just poll the API:

```bash
until [ "$(curl -s "http://localhost:8080/api/v1/sandboxes/$ID" \
            -H "authorization: bearer $TOKEN" | jq -r .status)" = "2" ]; do
  sleep 0.5
done
echo "Sandbox $ID is Running"
```

`status: 2` is `Running`. Once you see it, no other "stand-in" steps are needed — the worker has already done everything.

### 5. Fetch an SSH cert

```bash
ssh-keygen -t ed25519 -f /tmp/id_alcatraz -N "" -C "demo@workstation"
PUB=$(cat /tmp/id_alcatraz.pub)

RESP=$(curl -sX POST "http://localhost:8080/api/v1/sandboxes/$ID/ssh-cert" \
  -H "authorization: bearer $TOKEN" -H 'content-type: application/json' \
  -d "{\"sshPublicKey\":\"$PUB\"}")

echo "$RESP" | jq -r .cert > /tmp/id_alcatraz-cert.pub
GATEWAY_HOST=$(echo "$RESP" | jq -r .gatewayHost)
GATEWAY_PORT=$(echo "$RESP" | jq -r .gatewayPort)

ssh-keygen -L -f /tmp/id_alcatraz-cert.pub
# Type: user cert | Principal: <ID> | Valid: now → +24h | Signing CA: alcatraz-demo-ca
echo "Connect target: $GATEWAY_HOST:$GATEWAY_PORT"
```

In local dev (no `Gateway:Host` configured on the API), `gatewayHost`/`gatewayPort` is the worker-reported VM IP on the `172.16.0.0/24` bridge — directly reachable from the host because the worker runs in the host's network namespace.

In a `--profile gateway` deployment with `Gateway:Host=ssh.alcatraz.io`, those fields point at Traefik instead.

### 6. SSH into the sandbox

```bash
ssh -i /tmp/id_alcatraz \
    -i /tmp/id_alcatraz-cert.pub \
    -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
    -p $GATEWAY_PORT "al@$GATEWAY_HOST"
```

You should land in a shell as `al`. Inside the VM, confirm the chain:

```bash
cat /etc/ssh/auth_principals/al           # the sandbox UUID
cat /etc/ssh/trusted_user_ca_keys         # the API's CA pubkey
```

That's the proof: stock `ssh` + stock `sshd` accepted a cert minted by the API's CA, scoped to a specific sandbox UUID, with no shared keys ever changing hands and the trust files planted by the worker (not baked into the rootfs).

## Negative cases worth trying

| Scenario | Expected |
|---|---|
| `POST /sandboxes/$ID/ssh-cert` while still `Provisioning` | API returns failure: `Sandbox.InvalidStateForCertIssue`. Cert issuance now requires `Running`. |
| Worker not running when you create a sandbox | Stays in `Provisioning` forever; CLI's poll exhausts at 30s. Start the worker and the next attempt succeeds. |
| SSH with the cert *after* deleting the sandbox | Worker tears down Firecracker; bridge IP no longer responds. Cert is still cryptographically valid until TTL — production gateway/firewall handles revocation by removing the route. |
| SSH with a cert whose principal differs from `auth_principals/al` | `sshd` rejects: `Certificate invalid: name is not a listed principal`. |
| SSH after the cert has expired (24h) | `sshd` rejects with `expired`. |
| `POST /sandboxes/<id>/ssh-cert` for someone else's sandbox | API returns 404 (not 403) — owner-scoped lookup. |

## Cert pipeline only (no worker)

If you want to validate the cert pipeline in isolation — no KVM, no CNI, no Firecracker — bring up the legacy demo sshd stand-in:

```bash
docker compose --profile demo-sshd up -d
```

Because the API now hard-requires `Running` for cert issuance and there's no worker to publish `vm.ready`, you have to set the sandbox to `Running` directly in the database and write the principal file by hand. This is intentionally awkward — it's only useful for "did I break the cert handler?" sanity checks.

```bash
ID=...   # from /sandboxes
docker exec Alcatraz.Db psql -U postgres -d alcatraz \
  -c "UPDATE sandboxes SET status = 2, host = 'localhost', port = 2222 WHERE id = '$ID';"
docker exec Alcatraz.DemoSshd sh -c "echo $ID > /etc/ssh/auth_principals/al"
```

Then steps 5–6 of the walkthrough work against `localhost:2222`. Prefer the proper worker path for everything else.

## Resetting the stack

```bash
docker compose down -v   # drops keycloak_data, alcatraz_ca, traefik_dynamic, traefik_acme volumes
docker compose up -d --build
```

The CA key is regenerated, so any previously issued certs become useless — exactly the same property a real CA rotation would have.

## What this demo does *not* prove yet

- Public TLS ingress under load — the `--profile gateway` path is functional but doesn't get exercised by this walkthrough; do that on a host with real DNS.
- KRL-driven sub-TTL revocation.
- L2 isolation between VMs on a shared bridge (see [`../../alcatraz.worker/docs/network-isolation.md`](../../alcatraz.worker/docs/network-isolation.md)).
- Multi-host workers behind a NAT — the bridge subnet `172.16.0.0/24` is single-host today; multi-host needs a shared underlay.

Those are tracked in [`../../plans/end-to-end-wrap-up.md`](../../plans/end-to-end-wrap-up.md) § "Out of scope" and in [`../../plans/customer-vm-access-ssh-ca.md`](../../plans/customer-vm-access-ssh-ca.md).
