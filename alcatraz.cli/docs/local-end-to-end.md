# Local End-to-End Demo (CLI)

This walks through `alcatraz.cli` against the local stack: **device-flow login → create sandbox → wait for the worker to boot the Firecracker VM → fetch SSH cert → SSH into the sandbox → delete and watch it tear down**.

If you want the curl-only walkthrough that exercises the API directly, see [`../alcatraz.api/docs/local-end-to-end.md`](../../alcatraz.api/docs/local-end-to-end.md).

## Prerequisites

- Docker + Docker Compose
- `dotnet` SDK 8 (build-time only — the published binary is self-contained)
- `ssh`, `ssh-keygen`, and `jq` on the host
- A web browser (one click during device-flow login)
- KVM, CNI plugins, and a built kernel + rootfs in `alcatraz.core/` for the worker — see [`../../alcatraz.worker/README.md`](../../alcatraz.worker/README.md)

## 1. Bring up the stack

The full stack lives in the repo-root `docker-compose.yml`. The worker is host-run because it needs KVM and CNI.

```bash
cd /path/to/alcatraz   # repo root
docker compose up -d
docker compose ps
# expect: Alcatraz.{Api,Db,Identity,Redis,Nats,Seq} running
#         + Alcatraz.CaInit exited 0 (CA key generation)
```

EF migrations apply automatically on API startup — wait for `Now listening on: http://[::]:8080` in `docker compose logs alcatraz.api`.

Copy the API's CA pubkey out of the shared compose volume so the worker can plant it into each VM's overlay. `/run` is tmpfs, so re-run this after every host reboot — the worker fails fast at startup if the file is missing.

```bash
alcatraz.worker/scripts/sync-ca-pubkey.sh
```

Then build and run the worker:

```bash
cd alcatraz.worker
make build
sudo -E ./bin/alcatraz-worker
```

The worker subscribes to `vm.spawn` and `vm.destroy` and publishes `vm.ready` / `vm.destroyed` back to the API.

## 2. Build the CLI

Publish a self-contained single-file binary and put it on `PATH`. The output has no .NET runtime dependency at runtime.

```bash
alcatraz.cli/scripts/publish.sh
sudo ln -sf "$(pwd)/alcatraz.cli/dist/alcatraz" /usr/local/bin/alcatraz

alcatraz --version
```

Re-run `publish.sh` after editing CLI sources to refresh the binary. The script unlinks the previous file first, so an already-running `alcatraz ssh` session keeps working while you rebuild.

## 3. Configure (only if defaults don't match)

The CLI defaults to `http://localhost:8080`, which matches the root compose. To override:

```bash
mkdir -p ~/.config/alcatraz && chmod 0700 ~/.config/alcatraz
cat > ~/.config/alcatraz/config.json <<'JSON'
{ "apiBaseUrl": "http://localhost:8080", "alwaysUseGatewayProxy": false }
JSON
chmod 0600 ~/.config/alcatraz/config.json
```

## 4. Register a Keycloak user (one-time per stack)

The realm starts empty. Register a user — this creates a Keycloak account *and* the matching local `users` row in one transaction:

```bash
curl -sX POST http://localhost:8080/api/v1/users/register \
  -H 'content-type: application/json' \
  -d '{"email":"demo@alcatraz.local","firstName":"Demo","lastName":"User","password":"demopass"}'
```

Register is idempotent: re-running it for an existing email returns the existing user id rather than 500ing. If Keycloak has the user but the local DB row is missing (e.g. you wiped the DB volume but not Keycloak's), register will reconcile by creating the local row against the existing Keycloak identity.

## 5. Sign in

```bash
alcatraz login
```

A panel prints with the `userCode` and a verification URL; the CLI tries to open the browser automatically (use `--no-browser` to skip). Sign in as `demo@alcatraz.local` / `demopass`. The CLI polls the token endpoint and reports `Logged in.`

```bash
alcatraz whoami
```

## 6. Create a sandbox

```bash
ID=$(alcatraz sandbox create --vcpus 2 --memory 2048 --json | jq -r .id)
echo "$ID"
```

The API persists the row in `Provisioning`, writes a `SandboxRequested` outbox message in the same transaction, and the Quartz outbox drainer publishes `vm.spawn` on NATS. The worker picks it up, plants `auth_principals/al` and `trusted_user_ca_keys` into the AgentFS overlay, boots Firecracker, probes `vm_ip:22` until sshd accepts, and publishes `vm.ready`. The API's `VmReadyConsumer` consumes that and transitions the sandbox to `Running`.

Watch the events on NATS in another shell if you want to see them flow:

```bash
docker run --rm --network alcatraz_default natsio/nats-box:latest \
  nats sub 'vm.>' -s nats://alcatraz-nats:4222
```

```bash
alcatraz sandbox list
alcatraz sandbox get "$ID"
# wait until status reads Running (~10–15s on a warm host)
```

## 7. SSH

```bash
# Interactive shell
alcatraz ssh "$ID"

# One-shot remote command
alcatraz ssh "$ID" "whoami; uname -r; hostname"
```

The CLI re-issues the cert on each `ssh` invocation (cheap; 24h TTL means we never re-prompt for login mid-session) and writes it to `~/.config/alcatraz/certs/<id>-cert.pub`. Inspect it:

```bash
ssh-keygen -L -f ~/.config/alcatraz/certs/$ID-cert.pub
# Type=user cert | Principal=$ID | Valid: now → +24h | Signing CA: alcatraz-demo-ca
```

In local dev (no `Gateway:Host` configured on the API), the cert response carries the worker-reported VM IP on the `172.16.0.0/24` bridge — directly reachable from the host because the worker runs in the host's network namespace. In a `--profile gateway` deployment, the cert points at Traefik instead.

## 8. Delete

```bash
alcatraz sandbox delete "$ID"
```

Status transitions: `Running` → `Deleting` immediately (API publishes `vm.destroy`), then `Deleted` once the worker has stopped Firecracker, CNI has torn down the bridge, and the worker has published `vm.destroyed` (~5–10s end-to-end).

```bash
alcatraz sandbox get "$ID"
# expect: status=Deleted
```

If a VM exits unexpectedly (kernel panic, OOM, host kill) while in `Provisioning` or `Running`, the same `vm.destroyed` consumer transitions the sandbox to `Failed` instead.

## 9. Tear down

```bash
docker compose down
# Stop the worker (Ctrl-C in its terminal) or send SIGTERM:
sudo pkill -INT alcatraz-worker
```

Add `-v` to `docker compose down` to drop the Keycloak / CA / Traefik volumes too (any previously issued certs become useless when the CA volume regenerates).

## Negative cases worth trying

| Scenario | Expected |
|---|---|
| `alcatraz sandbox get <random-uuid>` | exit `4`, `Sandbox <id> was not found, or you don't have access to it.` |
| `alcatraz sandbox create --vcpus 0` | exit `3`, validation error from the Spectre `Settings.Validate()` |
| `alcatraz sandbox list` after `logout` | exit `2`, prompt to run `alcatraz login` |
| `alcatraz sandbox ssh-cert <id>` while still `Provisioning` | API returns `Sandbox.InvalidStateForCertIssue` (cert issuance requires `Running`) |
| Worker not running when you create a sandbox | Stays in `Provisioning` until the worker boots and drains the spawn message |
| `alcatraz ssh <id>` after deleting | Worker tears down Firecracker; bridge IP no longer responds. Cert is still cryptographically valid until TTL — production gateway/firewall handles revocation by removing the route |
| Lower the access-token TTL in Keycloak (60s) and wait, then run `alcatraz sandbox list` | the `BearerHandler` silently calls `POST /api/v1/auth/refresh` and the request succeeds without re-login |

## What this demo does *not* prove yet

- Public TLS ingress under load — the `--profile gateway` (Traefik) path is functional but doesn't get exercised here.
- KRL-driven sub-TTL revocation.
- L2 isolation between VMs on a shared bridge — see [`../../alcatraz.worker/docs/network-isolation.md`](../../alcatraz.worker/docs/network-isolation.md).
- Multi-host workers behind a NAT — the bridge subnet `172.16.0.0/24` is single-host today.

Those are tracked in [`../../plans/end-to-end-wrap-up.md`](../../plans/end-to-end-wrap-up.md) § "Out of scope" and in [`../../plans/customer-vm-access-ssh-ca.md`](../../plans/customer-vm-access-ssh-ca.md).
