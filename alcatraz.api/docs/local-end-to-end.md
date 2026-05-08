# Local End-to-End Demo

This walks through the entire customer flow against a local `docker compose` stack — **device-flow login → create sandbox → fetch SSH cert → SSH into the sandbox** — without writing the CLI app and without `alcatraz.gateway` or `alcatraz.worker` being involved.

A `forqstudio-demo-sshd` Alpine container stands in for a real Firecracker VM. It mirrors the rootfs from `alcatraz.core/build-rootfs.sh`: user `al` with passwordless sudo, sshd configured with `TrustedUserCAKeys` and `AuthorizedPrincipalsFile`. SSH-ing into it with the cert that `alcatraz.api` issued is the proof that the cert pipeline works end-to-end.

## Prerequisites

- Docker Compose
- `curl`, `jq`, `ssh-keygen`, `ssh` on the host
- A web browser (one click during device-flow login)

## What `docker compose up` brings up now

| Service | Purpose |
|---|---|
| `forqstudio.api` | Our API on `:8080` (ssh-keygen now installed in the image; CA volume mounted at `/run/alcatraz-ca`) |
| `forqstudio-db` | Postgres on `:5432` |
| `forqstudio-idp` | Keycloak on `:8082` (realm export pre-configured with device flow enabled) |
| `forqstudio-redis` | Redis on `:6379` |
| `forqstudio-nats` | NATS broker on `:4222` (monitoring on `:8222`) |
| `forqstudio-seq` | Seq log viewer on `:8083` |
| `forqstudio-ca-init` | Generates the shared CA key into the `alcatraz_ca` volume on first boot, then exits |
| `forqstudio-demo-sshd` | Demo Alpine container running `sshd` on `:2222`, mounts the CA pubkey from the shared volume |

## First-time bring-up

If you've previously run `docker compose up`, you must wipe the Keycloak volume so the updated realm (with device flow enabled) is re-imported. Keycloak only imports on first boot:

```bash
cd alcatraz.api
docker compose down -v
docker compose up -d --build
```

The first build takes a while (API is a multi-stage .NET image). Wait until logs settle:

```bash
docker compose logs -f forqstudio.api
# Wait for "Now listening on: http://[::]:8080"
```

Subsequent runs just need `docker compose up -d`. Re-run with `--build` whenever you edit `.files/ca-init/`, `.files/demo-sshd/`, or `src/ForqStudio.Api/Dockerfile`.

For the architecture that sits behind these endpoints — domain model, NATS contract, CA, error mapping — see `customer-cli-and-sandboxes.md`.

## End-to-end walkthrough

### 1. Register a Keycloak user (one-time per stack)

The realm ships empty of human users on purpose — registering through the API creates a Keycloak account *and* the matching local `users` row in one transaction. Without that, claims transformation can't resolve the local `UserId`.

```bash
curl -sX POST http://localhost:8080/api/v1/users/register \
  -H 'content-type: application/json' \
  -d '{"email":"demo@alcatraz.local","firstName":"Demo","lastName":"User","password":"demopass"}'
```

### 2. Get an access token

You have two options.

**Fast path (no browser, for verification scripts):** the existing `/users/login` endpoint runs the OAuth password grant against Keycloak.

```bash
TOKEN=$(curl -sX POST http://localhost:8080/api/v1/users/login \
  -H 'content-type: application/json' \
  -d '{"email":"demo@alcatraz.local","password":"demopass"}' | jq -r .accessToken)
```

**Real device flow path (what the CLI will actually do):**

```bash
INIT=$(curl -sX POST http://localhost:8080/api/v1/auth/device)
echo "$INIT" | jq .
DEVICE_CODE=$(echo "$INIT" | jq -r .deviceCode)

echo "Open this URL in a browser, sign in as demo@alcatraz.local / demopass:"
echo "  $(echo "$INIT" | jq -r .verificationUriComplete)"
read -p "Press Enter once you've signed in..."

TOKEN=$(curl -sX POST http://localhost:8080/api/v1/auth/device/token \
  -H 'content-type: application/json' \
  -d "{\"deviceCode\":\"$DEVICE_CODE\"}" | jq -r .accessToken)
echo "Got access token (truncated): ${TOKEN:0:40}..."
```

While polling, `POST /auth/device/token` returns HTTP 400 with `"error": "authorization_pending"` until the browser flow completes — that's the RFC 8628 idiom the CLI implements. The compose stack sets `KC_HOSTNAME_URL=http://localhost:8082` so the verification URL is reachable from the host browser; without it, Keycloak emits the docker-internal hostname.

### 3. Create a sandbox

```bash
SANDBOX=$(curl -sX POST http://localhost:8080/api/v1/sandboxes \
  -H "authorization: bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"vcpus":2,"memoryMib":2048}')
echo "$SANDBOX" | jq .
ID=$(echo "$SANDBOX" | jq -r .id)
```

Watch the spawn fire on NATS in another shell:

```bash
docker run --rm --network alcatrazapi_default natsio/nats-box:latest \
  nats sub vm.spawn -s nats://forqstudio-nats:4222
```

You should see the message arrive: `{"id":"<uuid>","vcpus":2,"memory_mib":2048,"customer_id":"<owner-uuid>"}`.

### 4. Stand-in for the worker: write the principal file on the demo VM

In production, the worker writes `/etc/ssh/auth_principals/al` (containing the sandbox UUID) into the AgentFS overlay before booting the VM. We do the same with `docker exec`:

```bash
docker exec ForqStudio.DemoSshd sh -c "echo $ID > /etc/ssh/auth_principals/al"
```

### 5. Fetch an SSH cert for our workstation pubkey

```bash
ssh-keygen -t ed25519 -f /tmp/id_alcatraz -N "" -C "demo@workstation"
PUB=$(cat /tmp/id_alcatraz.pub)

curl -sX POST "http://localhost:8080/api/v1/sandboxes/$ID/ssh-cert" \
  -H "authorization: bearer $TOKEN" -H 'content-type: application/json' \
  -d "{\"sshPublicKey\":\"$PUB\"}" | jq -r .cert > /tmp/id_alcatraz-cert.pub

ssh-keygen -L -f /tmp/id_alcatraz-cert.pub
# Confirm: Type: user cert | Principal: <ID> | Valid: now → +24h | Signing CA: alcatraz-demo-ca
```

### 6. SSH into the sandbox

```bash
ssh -i /tmp/id_alcatraz \
    -i /tmp/id_alcatraz-cert.pub \
    -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
    -p 2222 al@localhost
```

You should land in a shell as `al` on the demo container. `sudo whoami` returns `root` (passwordless sudo, mirrors the rootfs).

This is the proof: stock `ssh` + stock `sshd` accept a cert minted by `alcatraz.api`'s CA endpoint, scoped to a specific sandbox, with no shared keys ever changing hands. The gateway, when it lands, will be a TLS-terminating proxy in front of the same `sshd`-side handshake — so this same cert will work there too.

## Negative cases worth trying

| Scenario | Expected |
|---|---|
| SSH with the cert *after* deleting the sandbox | Fails: gateway in production would 404; here, the demo cert still works because we don't delete `auth_principals/al`. Run `docker exec ForqStudio.DemoSshd sh -c '> /etc/ssh/auth_principals/al'` to simulate the worker's cleanup. |
| SSH with a cert whose principal differs from `auth_principals/al` | `sshd` rejects: `Certificate invalid: name is not a listed principal`. Try issuing a cert for one sandbox ID but writing a different one into the file. |
| SSH after the cert has expired (24h) | `sshd` rejects with `expired`. |
| `POST /sandboxes/<id>/ssh-cert` for someone else's sandbox | API returns 404 (not 403) — owner-scoped lookup. |

## Resetting the stack

```bash
docker compose down -v   # drops keycloak_data and alcatraz_ca volumes
docker compose up -d --build
```

The CA key is regenerated, so any previously issued certs become useless — exactly the same property a real CA rotation would have.

## What this demo does *not* prove yet

- Multiplexing many sandboxes onto one TLS endpoint (`alcatraz.gateway`).
- Worker → API endpoint reporting (the sandbox stays in `Provisioning` forever).
- KRL-driven sub-TTL revocation.
- L2 isolation between VMs on a shared bridge.

Those are tracked in `docs/customer-cli-and-sandboxes.md` § "Where to continue" and in `plans/customer-vm-access-ssh-ca.md`.
