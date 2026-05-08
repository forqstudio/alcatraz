# Local End-to-End Demo (CLI)

This walks through `alcatraz.cli` against the local stack: **device-flow login → create sandbox → fetch SSH cert → SSH into the sandbox** — using the actual CLI rather than `curl`.

If you want the curl-only walkthrough that exercises the API directly, see [`../alcatraz.api/docs/local-end-to-end.md`](../../alcatraz.api/docs/local-end-to-end.md).

## Prerequisites

- Docker + Docker Compose
- `dotnet` SDK 8
- `ssh`, `ssh-keygen`, and `openssl` on the host
- A web browser

## 1. Bring up the stack

The full stack lives in the repo-root `docker-compose.yml`. The worker is host-run.

```bash
cd /path/to/alcatraz   # repo root
docker compose up -d
docker compose ps
# expect: ForqStudio.{Api,Db,Identity,Redis,Nats,Seq,DemoSshd} running
#         + ForqStudio.CaInit exited 0 (CA key generation)
```

EF migrations apply automatically on API startup — wait for `Now listening on: http://[::]:8080` in `docker compose logs forqstudio.api`.

## 2. Build the CLI

```bash
dotnet build alcatraz.cli/Alcatraz.Cli.sln
```

(Or use `dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- <args>` directly — it's not strictly necessary to pre-build.)

## 3. Configure (only if defaults don't match)

The CLI defaults to `http://localhost:8080`, which matches the root compose. If you need to override:

```bash
mkdir -p ~/.config/alcatraz && chmod 0700 ~/.config/alcatraz
cat > ~/.config/alcatraz/config.json <<'JSON'
{ "apiBaseUrl": "http://localhost:8080", "alwaysUseGatewayProxy": false }
JSON
chmod 0600 ~/.config/alcatraz/config.json
```

## 4. Register a Keycloak user

The realm starts empty. Register a user via the API (this creates both a Keycloak account and the local `users` row in one transaction):

```bash
curl -sX POST http://localhost:8080/api/v1/users/register \
  -H 'content-type: application/json' \
  -d '{"email":"demo@alcatraz.local","firstName":"Demo","lastName":"User","password":"demopass"}'
```

## 5. Sign in

```bash
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- login
```

A panel prints with the `userCode` and a verification URL; the CLI will try to open the browser automatically (use `--no-browser` to skip). Sign in as `demo@alcatraz.local` / `demopass`. The CLI polls the token endpoint and reports `Logged in.`

```bash
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- whoami
```

## 6. Create + inspect a sandbox

```bash
ID=$(dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- sandbox create \
       --vcpus 2 --memory 2048 --json | jq -r .id)

dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- sandbox list
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- sandbox get "$ID"
```

## 7. Wire the demo sshd to the sandbox UUID

In production, the worker writes `/etc/ssh/auth_principals/al` containing the sandbox UUID into the AgentFS overlay before booting the VM. For the local demo we do the same with `docker exec`:

```bash
docker exec ForqStudio.DemoSshd sh -c "echo $ID > /etc/ssh/auth_principals/al"
```

## 8. SSH

```bash
# Interactive shell
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- ssh "$ID"

# One-shot remote command
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- ssh "$ID" "whoami; uname -s; hostname"
```

The CLI re-issues the cert on each `ssh` invocation (cheap; 24h TTL means we never re-prompt for login mid-session) and writes it to `~/.config/alcatraz/certs/<id>-cert.pub`.

Inspect the cert:

```bash
ssh-keygen -L -f ~/.config/alcatraz/certs/$ID-cert.pub
# expect Type=user cert, Principal=$ID, Valid: now → +24h
```

## 9. Delete

```bash
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- sandbox delete "$ID"
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- sandbox list
# the sandbox is now in `Deleting`
```

## 10. Tear down

```bash
docker compose down
```

## Negative cases worth trying

| Scenario | Expected |
|---|---|
| `alcatraz sandbox get <random-uuid>` | exit `4`, `Sandbox <id> was not found, or you don't have access to it.` |
| `alcatraz sandbox create --vcpus 0` | exit `3`, validation error from the Spectre `Settings.Validate()` |
| `alcatraz sandbox list` after `logout` | exit `2`, prompt to run `alcatraz login` |
| `alcatraz ssh <id>` while the demo sshd's `auth_principals/al` does not contain `<id>` | sshd rejects: `Certificate invalid: name is not a listed principal` |
| Lower the access-token TTL in Keycloak (60s) and wait, then run `alcatraz sandbox list` | the `BearerHandler` silently calls `POST /api/v1/auth/refresh` and the request succeeds without re-login |

## What this demo does *not* prove yet

- Multiplexing many sandboxes onto one TLS endpoint (`alcatraz.gateway`).
- Worker → API endpoint reporting (the sandbox stays in `Provisioning` forever).
- KRL-driven sub-TTL revocation.
- L2 isolation between VMs on a shared bridge.

Those are tracked in [`../../plans/customer-vm-access-ssh-ca.md`](../../plans/customer-vm-access-ssh-ca.md).
