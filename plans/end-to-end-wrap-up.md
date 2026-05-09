# Wrap up the end-to-end SSH-into-Firecracker flow (production-ready demo)

## Context

The customer flow is broken at two seams. First, after `alcatraz.worker` boots a Firecracker VM, nothing tells `alcatraz.api` where to reach it — the sandbox stays in `Provisioning` forever and the cert response carries a hard-coded demo gateway. Second, there is no public ingress: today the README walks through `localhost:2222` against a stand-in container, which proves the cert pipeline works but isn't reachable from another laptop.

The wrap-up closes both seams in one pass so the system is demoable from another laptop over the public internet:

1. **Worker → API feedback (NATS)** — worker publishes `vm.ready` after boot; API has a hosted background consumer that updates the sandbox row. API and worker stay strangers; they only know NATS subjects.
2. **Overlay writes before boot** — worker writes `/etc/ssh/auth_principals/al` (sandbox UUID) and `/etc/ssh/trusted_user_ca_keys` (the API's CA pubkey) into the AgentFS overlay before `m.Start()`. Spawn aborts on write failure.
3. **Traefik public ingress + `alcatraz.routes` publisher** — Traefik handles TLS termination, ACME via Let's Encrypt, SNI-based TCP routing, and raw byte forwarding to the VM's sshd. We don't roll our own gateway. A small Go service (`alcatraz.routes`) subscribes to `vm.ready`/`vm.destroyed` on NATS and writes Traefik's dynamic file provider config; Traefik hot-reloads on change.

Customer flow once shipped: `alcatraz login` → `sandbox create` → `alcatraz ssh <id>` opens stock `ssh` whose `ProxyCommand` runs `openssl s_client -connect ssh.alcatraz.io:443 -servername <id>`. Traefik accepts the TLS, matches the SNI against a per-sandbox TCP router, splices to the VM's sshd. sshd validates the cert against the API's CA pubkey + the sandbox-UUID principal. Customer lands in a shell.

The same code paths support a local-only dev mode (no Traefik, CLI talks directly to `172.16.0.x:22`) by toggling one config: `Gateway` options unset → cert response returns the per-sandbox VM endpoint; set → returns Traefik's public address.

## Plan

### Phase 1 — Worker → API readiness pipeline

#### 1.1 Domain — `Sandbox.MarkRunning` + endpoint fields
- `alcatraz.api/src/Alcatraz.Domain/Sandboxes/Sandbox.cs`
  - Add private-setter properties `string? Host`, `int? Port`.
  - `Result MarkRunning(string host, int port, DateTime utcNow)` — requires `Status == Provisioning`, sets host/port, transitions to `Running`, raises `SandboxBecameRunningDomainEvent`.
  - Tighten `CanIssueCertificate()` to `Status == Running` (cleaner invariant; CLI polls until Running).
- `alcatraz.api/src/Alcatraz.Domain/Sandboxes/SandboxErrors.cs` — add `NotProvisioning`, `NotReady`.
- `alcatraz.api/src/Alcatraz.Domain/Sandboxes/Events/SandboxBecameRunningDomainEvent.cs` *(new)*.
- `alcatraz.api/src/Alcatraz.Infrastructure/Configurations/SandboxConfiguration.cs` — map `host` (nullable text), `port` (nullable int).
- EF migration: `dotnet ef migrations add Add_Sandbox_Endpoint --project src/Alcatraz.Infrastructure --startup-project src/Alcatraz.Api`. Applied on startup in Development via `app.ApplyMigrations()`.

#### 1.2 Application — `MarkSandboxRunningCommand`
- `alcatraz.api/src/Alcatraz.Application/Sandboxes/MarkSandboxRunning/`
  - `MarkSandboxRunningCommand.cs` — `record MarkSandboxRunningCommand(Guid SandboxId, string Host, int Port) : ICommand`.
  - `MarkSandboxRunningCommandHandler.cs` — load sandbox, call `MarkRunning`, `SaveChangesAsync`. Idempotent on retry.
  - `MarkSandboxRunningCommandValidator.cs` — `SandboxId` non-empty, `Host` non-empty, `Port` in [1, 65535].

#### 1.3 Cert handler — return gateway-or-sandbox endpoint
- `alcatraz.api/src/Alcatraz.Application/Sandboxes/IssueSshCertificate/IssueSshCertificateCommandHandler.cs`
  - Keep `IOptions<GatewayOptions>` injection.
  - Logic: if `gatewayOptions.Host` is non-empty, return `(gatewayOptions.Host, gatewayOptions.Port)` (production via Traefik). Otherwise return `(sandbox.Host!, sandbox.Port!)` (local dev). Both branches require `Status == Running` so endpoint is set.
  - Wire field names `GatewayHost`/`GatewayPort` stay — CLI is unchanged on the wire.

#### 1.4 Infrastructure — NATS consumer for `vm.ready`
- `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/NatsOptions.cs` — add `ReadySubject = "vm.ready"`, `ReadyQueueGroup = "api-vm-ready"`.
- `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/VmReadyConsumer.cs` *(new)* — `BackgroundService`. Gets connection from `NatsConnectionFactory`, `await foreach` over `connection.SubscribeAsync<byte[]>(subject, queueGroup: ..., ct)`. Per message: deserialize `{id, host, port}` (snake_case), create scope from `IServiceScopeFactory`, dispatch `MarkSandboxRunningCommand` via `ISender`. Logs failures, never crashes the loop.
- `alcatraz.api/src/Alcatraz.Infrastructure/DependencyInjection.cs` — `services.AddHostedService<VmReadyConsumer>();` in `AddSandboxIntegrations`.

#### 1.5 Worker — overlay writes (auth_principals + CA pubkey)
- `alcatraz.worker/internal/vm/agentfs/overlay.go` — add method on `*OverlayHandle`:
  ```go
  func (h *OverlayHandle) WriteFile(ctx context.Context, path string, data []byte, mode os.FileMode) error {
      dir := filepath.ToSlash(filepath.Dir(path))
      if dir != "" && dir != "." && dir != "/" {
          if err := h.overlay.MkdirAll(ctx, dir, int64(sdk.S_IFDIR|0o755)); err != nil {
              return fmt.Errorf("mkdir %s: %w", dir, err)
          }
      }
      return h.overlay.WriteFile(ctx, path, data, int64(sdk.S_IFREG|(mode&0o7777)))
  }
  ```
- `alcatraz.worker/internal/vm/spawn.go` — between `agentfs.PrepareOverlay` (line 66) and `m.Start(ctx)` (line 147), open the overlay via `agentfs.OpenOverlay`, write both:
  - `/etc/ssh/auth_principals/al` ← `<vm_id>\n`, mode 0644
  - `/etc/ssh/trusted_user_ca_keys` ← contents of `WORKER_CA_PUBKEY_PATH`, mode 0644

  Close. Sequence is safe: writes happen before the NFS server binds (`StartNFS` runs in `AppendAfter(SetupNetworkHandlerName)` during `m.Start`). The deferred cleanup wipes the overlay DB on the failure path, so a partial write disappears.
- `alcatraz.worker/internal/vm/config.go` — add `CAPubkeyPath` to spawn options; surface env `WORKER_CA_PUBKEY_PATH` (default `/run/alcatraz-ca/alcatraz_ca.pub`). Operator copies the pubkey from the compose `alcatraz_ca` volume to that host path (or sets the env to the volume mount on the host).

#### 1.6 Worker — publish `vm.ready` (and `vm.destroyed` for Phase 2)
- `alcatraz.worker/internal/messaging/config.go` — `ReadySubject` (env `NATS_READY_SUBJECT`, default `vm.ready`), `DestroyedSubject` (env `NATS_DESTROYED_SUBJECT`, default `vm.destroyed`). Note `vm.destroyed` (post-exit event) is distinct from API's request subject `vm.destroy`.
- `alcatraz.worker/internal/messaging/publisher.go` *(new)* — `Publisher` with its own `*nats.Conn`. Methods: `NewPublisher(url)`, `PublishVMReady(ctx, id, host, port)`, `PublishVMDestroyed(ctx, id)`, `Close()` calling `Drain`. Don't refactor the subscriber to share — three lines beats premature abstraction; `*nats.Conn` is concurrency-safe, second TCP socket negligible.
- `alcatraz.worker/cmd/alcatraz-worker/main.go` — construct publisher next to subscriber, `defer publisher.Close()`. After `vm.Spawn` returns, call `publisher.PublishVMReady(ctx, id, vmIP, 22)` synchronously; log+continue on failure.
- `alcatraz.worker/internal/vm/spawn.go`:
  - Before publishing, do a TCP probe on `vm_ip:22` in a 200ms tick loop with 10s timeout, so `vm.ready` means *actually* ready. Warn-log on timeout; publish anyway.
  - In the post-exit cleanup goroutine (the `m.Wait` block, line 172), publish `vm.destroyed` with `{id}` after `RemoveVirtualMachine`.

#### 1.7 CLI — poll until Running
- `alcatraz.cli/src/Alcatraz.Cli/Commands/Ssh/SshCommand.cs` — replace bare `GetSandboxAsync` preflight with poll-until-Running: every 500 ms call `GetSandboxAsync`, return when `Status == Running`, fail at ~30 s with a clear timeout. Wrap in a Spectre.Console `Status` spinner ("Waiting for sandbox to be ready…").

### Phase 2 — Public ingress via Traefik

#### 2.1 Traefik as a compose service
- Add to `docker-compose.yml`, gated behind a `gateway` profile so local dev doesn't pull it in:
  ```yaml
  alcatraz-traefik:
    image: traefik:3.1
    profiles: ["gateway"]
    network_mode: host          # so it can dial 172.16.0.0/24 on the worker host
    volumes:
      - ./.containers/traefik/traefik.yml:/etc/traefik/traefik.yml:ro
      - traefik_dynamic:/etc/traefik/dynamic
      - traefik_acme:/var/lib/traefik/acme
    restart: unless-stopped
  ```
  (`network_mode: host` is necessary because Firecracker's CNI bridge `alcatraz0` lives in the worker's host namespace; a Docker-networked Traefik would not have a route to `172.16.0.0/24`. For the single-host demo this is the simple right answer.)
- Static config: `.containers/traefik/traefik.yml`
  ```yaml
  entryPoints:
    wss:
      address: ":443"
  providers:
    file:
      directory: /etc/traefik/dynamic
      watch: true
  certificatesResolvers:
    letsencrypt:
      acme:
        email: ops@alcatraz.io
        storage: /var/lib/traefik/acme/acme.json
        tlsChallenge: {}        # TLS-ALPN-01 — same :443, no port 80 needed
  log:
    level: INFO
  accessLog: {}
  ```
- Bootstrap dynamic file (so Traefik starts cleanly with no routes): `.containers/traefik/dynamic.bootstrap.yml` containing only the default `tls.options` block. Compose copies this into the volume on first up via a one-shot init container if needed.

#### 2.2 `alcatraz.routes` — NATS → Traefik dynamic file
New small Go service that owns the Traefik dynamic config file.

```
alcatraz.routes/
├── cmd/alcatraz-routes/main.go
├── internal/
│   ├── config/        # env: NATS_URL, OUTPUT_PATH, GATEWAY_DOMAIN, DEBOUNCE_MS
│   ├── registry/      # in-memory map[id]{host, port}, RWMutex
│   ├── writer/        # debounced YAML writer
│   └── logging/       # mirror worker's slog/Seq setup
├── Dockerfile
├── go.mod
└── Makefile
```

- Subscribes to `vm.ready` (no queue group — fanout, every replica needs full state) and `vm.destroyed`.
- Maintains in-memory registry. On every change, debounces (~500 ms) and writes a single YAML file to `OUTPUT_PATH` (e.g. `/etc/traefik/dynamic/sandboxes.yml`).
- File shape per sandbox:
  ```yaml
  tcp:
    routers:
      sb-<id>:
        entryPoints: [wss]
        rule: "HostSNI(`<id>`)"
        service: sb-<id>
        tls:
          certResolver: letsencrypt
          domains:
            - main: ssh.alcatraz.io
      # ...one per sandbox
    services:
      sb-<id>:
        loadBalancer:
          servers:
            - address: "172.16.0.10:22"
  ```
  All sandboxes share the same single ACME cert for `ssh.alcatraz.io` (the `domains` block under each router pins ACME to that name regardless of SNI). Traefik serves that cert by default; openssl s_client doesn't enforce SAN match against SNI in `s_client` mode, so this works cleanly for the SSH-over-TLS path. (Future hardening: switch to wildcard `*.ssh.alcatraz.io` via DNS-01 ACME and use `<id>.ssh.alcatraz.io` SNI. Out of scope for now.)
- Compose service:
  ```yaml
  alcatraz-routes:
    build: ./alcatraz.routes
    profiles: ["gateway"]
    environment:
      NATS_URL: nats://alcatraz-nats:4222
      OUTPUT_PATH: /output/sandboxes.yml
      GATEWAY_DOMAIN: ssh.alcatraz.io
    volumes:
      - traefik_dynamic:/output
    depends_on: [alcatraz-nats]
    restart: unless-stopped
  ```
- Atomic write: write to `sandboxes.yml.tmp` then `os.Rename` so Traefik never reads a half-written file.

#### 2.3 CLI — set SNI on the openssl ProxyCommand
- `alcatraz.cli/src/Alcatraz.Cli/Commands/Ssh/SshLauncher.cs` — when the gateway-proxy form is used (port 443 or `alwaysUseGatewayProxy`), append `-servername <sandboxId>` to the `openssl s_client` arguments. SNI = sandbox UUID is what Traefik routes on.

#### 2.4 API config for production
- On the public host, set `Gateway:Host=ssh.alcatraz.io`, `Gateway:Port=443` (env: `Gateway__Host`, `Gateway__Port`). Cert handler then returns the gateway address. For local compose without Traefik: leave empty so handler falls back to `Sandbox.Host/Port`.

#### 2.5 Deployment topology (single public host for the demo)
- Host: a VPS with public IP and DNS A record `ssh.alcatraz.io` → that IP. Inbound `:443` open to the world; `:22` open only to operator IPs (or disabled).
- On the host:
  - `docker compose --profile gateway up -d` runs API + Keycloak + Postgres + Redis + NATS + Seq + ca-init + Traefik + alcatraz.routes.
  - `sudo -E ./bin/alcatraz-worker` runs as a host process (needs KVM + CNI + bridge to `172.16.0.0/24`). Traefik (in `network_mode: host`) shares that namespace, so it can dial VM IPs directly.
- One-time operator setup: copy CA pubkey from compose volume to a host path the worker can read:
  ```
  docker run --rm -v alcatraz_ca:/ca alpine cat /ca/alcatraz_ca.pub > /run/alcatraz-ca/alcatraz_ca.pub
  ```

### Phase 3 — Verify rootfs trust chain

The Firecracker rootfs in `alcatraz.core` must already have `sshd_config` configured with:
- `TrustedUserCAKeys /etc/ssh/trusted_user_ca_keys`
- `AuthorizedPrincipalsFile /etc/ssh/auth_principals/%u`
- `PubkeyAuthentication yes`, `PasswordAuthentication no`
- A login user `al` with `/bin/bash` (the certificate principal is the sandbox UUID; the SSH *user* is `al` per `SshLauncher`).

Read `alcatraz.core/build-rootfs.sh` and the rootfs's `sshd_config` template. If any of those four are missing, add them to the build script. The auth_principals and trusted_user_ca_keys *files themselves* are written by the worker into the overlay at spawn (Phase 1.5), so the build only needs to ensure the directories exist and sshd is configured to look there.

## Critical files

| Concern | File |
|---|---|
| Domain transition + endpoint storage | `alcatraz.api/src/Alcatraz.Domain/Sandboxes/Sandbox.cs` |
| EF mapping | `alcatraz.api/src/Alcatraz.Infrastructure/Configurations/SandboxConfiguration.cs` |
| Inbound NATS consumer (API) | `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/VmReadyConsumer.cs` *(new)* |
| Cert response sourcing | `alcatraz.api/src/Alcatraz.Application/Sandboxes/IssueSshCertificate/IssueSshCertificateCommandHandler.cs` |
| Overlay file write helper | `alcatraz.worker/internal/vm/agentfs/overlay.go` |
| Worker spawn (overlay writes + ready/destroyed publish) | `alcatraz.worker/internal/vm/spawn.go` |
| Worker NATS publisher | `alcatraz.worker/internal/messaging/publisher.go` *(new)* |
| Traefik static config | `.containers/traefik/traefik.yml` *(new)* |
| Compose: Traefik + routes services | `docker-compose.yml` |
| Routes publisher (NATS → dynamic.yml) | `alcatraz.routes/` *(new component)* |
| CLI polling | `alcatraz.cli/src/Alcatraz.Cli/Commands/Ssh/SshCommand.cs` |
| CLI SNI on ProxyCommand | `alcatraz.cli/src/Alcatraz.Cli/Commands/Ssh/SshLauncher.cs` |
| Rootfs sshd config (verify/patch) | `alcatraz.core/build-rootfs.sh` + the rootfs sshd_config template |

## Reused existing pieces

- `NatsConnectionFactory` (singleton, lazy connect) for the API consumer — no new connection management.
- `JsonNamingPolicy.SnakeCaseLower` — same convention as `NatsSandboxEventPublisher`.
- `agentfs.OpenOverlay` — already used in `PrepareOverlay` and `OpenAndServe`; we open one extra time between them.
- `SshCertificateResponse.GatewayHost/GatewayPort` field names — kept stable, CLI unchanged on the wire.
- `SshLauncher`'s existing `gatewayPort == 443` branch — already produces the `openssl s_client` ProxyCommand; we just add `-servername`.
- Compose profiles — already a documented mechanism in modern compose; lets local dev skip Traefik with no flag.

## Out of scope (called out for clarity)

- **Multi-host production routing.** This plan targets a single public host where worker and Traefik share the bridge namespace. Multi-host workers behind a NAT need an underlay (BGP / WireGuard / VPC routing) so Traefik can reach `172.16.0.0/24` on each worker. Defer.
- **Wildcard cert via DNS-01 ACME.** Single-cert-with-default approach is enough for the demo. Wildcard (`*.ssh.alcatraz.io`) would need DNS provider API access; revisit when adding strict TLS hostname verification.
- **KRL sub-TTL revocation.** Cert TTL (24h) handles steady-state revocation.
- **Traefik HA.** Single replica fine for the demo. With more replicas, the no-queue-group fanout subscription on `alcatraz.routes` is correct; add a TCP load balancer in front.
- **Rate limiting / DoS.** Defer to upstream LB or Cloudflare. Traefik plugins exist if needed.

## Verification

1. **Unit tests** — `Sandbox.MarkRunning` Provisioning→Running + NotProvisioning failure. Worker `agentfs/integration_test.go` extended with overlay write-close-reopen-read assertion. `alcatraz.routes`: registry + YAML-writer unit tests asserting valid Traefik dynamic config shape.
2. **Local end-to-end (no gateway profile)**:
   ```
   docker compose up -d --build
   sudo -E ./alcatraz.worker/bin/alcatraz-worker
   alcatraz login && alcatraz sandbox create --vcpus 2 --memory 2048
   alcatraz ssh <id>           # CLI polls, then ssh's directly to 172.16.0.x:22
   ```
   Inside the VM: `cat /etc/ssh/auth_principals/al` matches the sandbox UUID; `cat /etc/ssh/trusted_user_ca_keys` matches the API's CA pubkey.
3. **Public-host end-to-end (gateway profile)**:
   - On the public host: `docker compose --profile gateway up -d` and `sudo -E ./bin/alcatraz-worker`.
   - From a remote laptop: `alcatraz login` (against `https://api.alcatraz.io`), `sandbox create`, `alcatraz ssh <id>`. CLI builds `openssl s_client -connect ssh.alcatraz.io:443 -servername <id>`. Drops into a shell.
   - Confirm via Traefik access logs: `<remote-ip> ... HostSNI=<id> ServiceName=sb-<id> ServiceURL=172.16.0.x:22`.
   - Confirm `alcatraz.routes` logs: `wrote N routes after vm.ready id=<id> host=172.16.0.x`.
4. **Negative cases**:
   - SNI for an unknown sandbox → Traefik 404s the TCP router (connection drops cleanly) and logs the miss.
   - Worker not running → CLI polling exhausts at 30 s with clear timeout, sandbox stays Provisioning.
   - VM destroyed → `vm.destroyed` removes the registry entry, `alcatraz.routes` rewrites file without the route, Traefik hot-reloads, subsequent connect attempts fail at SNI match.
   - Cert principal mismatch → in-VM sshd rejects (proves the auth_principals + CA chain works as designed).
