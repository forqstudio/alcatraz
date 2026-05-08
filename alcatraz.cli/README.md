# alcatraz.cli

The customer-facing CLI for [Alcatraz](../README.md). Built on .NET 8 + Spectre.Console.Cli.

`alcatraz.cli` is the entry point a customer uses to log in via OAuth device flow, manage sandboxes, fetch a short-lived SSH cert, and SSH into a Firecracker microVM. It holds no server state and no long-lived secrets.

---

## Where this sits in Alcatraz

```
alcatraz.cli ──HTTP──► alcatraz.api ──proxy──► Keycloak (device flow)
             ──HTTP──► alcatraz.api ──NATS──► alcatraz.worker ──► Firecracker VM
             ──HTTP──► alcatraz.api  (poll until Status == Running, then issue cert)
             ──ssh + ProxyCommand=openssl s_client … -servername <id> ──► Traefik ──► sshd in VM
                                                                                       (TrustedUserCAKeys = api's CA pubkey)
```

The CLI never talks to Keycloak directly — it goes through `alcatraz.api`, which proxies the device-code grant and the refresh-token grant. That's the property that lets the CLI ship without baking in Keycloak's `client_secret`.

`alcatraz ssh <id>` polls the API until the worker has reported the sandbox as `Running` (then the cert response carries the right endpoint). For local dev (no Traefik) it dials the per-sandbox VM IP directly; for production it sets the SNI on `openssl s_client` to the sandbox UUID so Traefik can route by it.

For the system-level design of how SSH access works, see [`../plans/customer-vm-access-ssh-ca.md`](../plans/customer-vm-access-ssh-ca.md).

---

## Commands

```
alcatraz login [--no-browser]
alcatraz logout
alcatraz whoami

alcatraz sandbox create [--vcpus N] [--memory MIB]
alcatraz sandbox list
alcatraz sandbox get <id>
alcatraz sandbox delete <id>
alcatraz sandbox ssh-cert <id> [--public-key PATH] [--out PATH]

alcatraz ssh <id> [remote-command] [--no-proxy]
```

Every command also accepts:

- `--api-url <URL>` — override the API base (default from config or `http://localhost:8080`).
- `--json` — emit machine-readable JSON instead of a Spectre table (where applicable).
- `-v` / `--verbose` — verbose logging.

---

## Configuration & state

Files live under `~/.config/alcatraz` on Linux/macOS (`%AppData%\alcatraz` on Windows):

| File | Purpose |
|---|---|
| `config.json` | `{ "apiBaseUrl": "...", "alwaysUseGatewayProxy": false }` |
| `tokens.json` | Cached `accessToken` / `refreshToken` / expiry (mode `0600`) |
| `id_alcatraz`, `id_alcatraz.pub` | Workstation ed25519 keypair, auto-generated on first cert request |
| `certs/<id>-cert.pub` | Cached SSH cert per sandbox (24h TTL) |
| `known_hosts` | Profile-local known-hosts file (kept out of `~/.ssh/`) |

Configuration layering (lowest → highest precedence): defaults → `config.json` → `ALCATRAZ_*` environment variables → `--api-url <url>` flag.

---

## Local development

The control plane and supporting services are brought up by the repo-root [`docker-compose.yml`](../docker-compose.yml). The worker runs separately on the host (it needs KVM and CNI). The CLI is built locally.

```bash
# 1. Bring up the stack and start the worker — see ../README.md for full setup
cd ..
docker compose up -d
sudo -E ./alcatraz.worker/bin/alcatraz-worker        # in another terminal

# 2. Build the CLI
dotnet build alcatraz.cli/Alcatraz.Cli.sln

# 3. Register a user (one-time, since the local realm starts empty)
curl -sX POST http://localhost:8080/api/v1/users/register \
  -H 'content-type: application/json' \
  -d '{"email":"demo@alcatraz.local","firstName":"Demo","lastName":"User","password":"demopass"}'

# 4. Sign in
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- login

# 5. Create + connect. The CLI polls until the worker reports Running,
#    issues a cert, and execs ssh.
ID=$(dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- sandbox create --vcpus 2 --memory 2048 --json | jq -r .id)
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- ssh "$ID"
```

A `curl`-only walkthrough (useful for smoke-testing the API in isolation) lives at [`../alcatraz.api/docs/local-end-to-end.md`](../alcatraz.api/docs/local-end-to-end.md).

---

## Project structure

The tree is laid out as feature slices, mirroring `alcatraz.api`'s `Application/` idiom: verb-first folders/classes, plural aggregate folders (`Sandboxes/`), shared types at the aggregate root, and topical sub-folders under `Common/` for cross-cutting infrastructure.

```
src/Alcatraz.Cli/
├── Program.cs                                    # → CliBootstrap.RunAsync(args)
├── Commands/
│   ├── Login/                                    # alcatraz login
│   │   ├── LoginCommand.cs / LoginSettings.cs
│   │   ├── DeviceFlowOrchestrator.cs / BrowserLauncher.cs
│   │   └── DeviceAuthorizationResponse.cs / DeviceTokenResponse.cs / DeviceTokenExchangeResult.cs / DeviceTokenError.cs
│   ├── Logout/                                   # alcatraz logout
│   │   └── LogoutCommand.cs
│   ├── WhoAmI/                                   # alcatraz whoami
│   │   ├── WhoAmICommand.cs
│   │   └── JwtPayloadDecoder.cs
│   ├── Sandboxes/                                # alcatraz sandbox <verb>
│   │   ├── SandboxIdSettings.cs / SandboxResponse.cs / SandboxRenderer.cs   # shared at aggregate root
│   │   ├── CreateSandbox/CreateSandboxCommand.cs + CreateSandboxSettings.cs
│   │   ├── ListSandboxes/ListSandboxesCommand.cs
│   │   ├── GetSandbox/GetSandboxCommand.cs
│   │   ├── DeleteSandbox/DeleteSandboxCommand.cs
│   │   └── IssueSshCertificate/IssueSshCertificateCommand.cs + Settings + SshCertificateResponse
│   └── Ssh/                                      # alcatraz ssh
│       ├── SshCommand.cs / SshSettings.cs
│       └── SshLauncher.cs
└── Common/
    ├── Bootstrap/                                # CliBootstrap, DI, Spectre type-registrar, ctrl-c
    ├── Cli/                                      # GlobalSettings, ExitCodes, CommandRunner
    ├── Configuration/                            # path resolver, options, config.json store
    ├── Authentication/                           # token store, BearerHandler
    ├── Api/                                      # IAlcatrazApiClient typed HttpClient + ApiErrors
    └── Ssh/                                      # SshKeyManager, CertificateCache (shared by ssh + ssh-cert)
test/Alcatraz.Cli.UnitTests/                      # mirrors src/, plus TempConfigDir fixture
```

---

## Tech stack

| Concern | Library |
|---|---|
| Runtime | .NET 8 |
| CLI | Spectre.Console.Cli |
| HTTP | `Microsoft.Extensions.Http` (typed `HttpClient`) |
| Config | `Microsoft.Extensions.Configuration` (defaults / file / env / flag layering) |
| SSH | shells out to stock `ssh` and `ssh-keygen` |
| Testing | xUnit + FluentAssertions + NSubstitute + RichardSzalay.MockHttp |

---

## Tests

```bash
dotnet test alcatraz.cli/Alcatraz.Cli.sln
```

The full design and the implementation plan are in [`../plans/alcatraz-cli.md`](../plans/alcatraz-cli.md).
