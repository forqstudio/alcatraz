# alcatraz.routes

NATS → Traefik dynamic-config bridge. Subscribes to `vm.ready` and `vm.destroyed`, maintains an in-memory sandbox→endpoint table, and atomically writes Traefik's file-provider YAML so Traefik can hot-reload.

`alcatraz.routes` is the only thing that knows both NATS subjects and Traefik's config schema. Workers don't know Traefik exists; the API doesn't know the gateway exists. Everyone meets at NATS.

## Where it sits

```
alcatraz.worker  ──── NATS vm.ready / vm.destroyed ────▶  alcatraz.routes
                                                          (in-memory map)
                                                                │
                                                                │ atomic write
                                                                ▼
                                              traefik_dynamic/sandboxes.yml
                                                                ▲
                                                                │ file watch (hot-reload)
                                                          Traefik (:443)
                                                          • TLS termination + ACME
                                                          • SNI = sandbox UUID → backend host:port
```

`vm.ready` is consumed without a queue group — every replica needs the full table; `vm.destroyed` likewise. With multiple replicas, every gateway sees every event independently.

## What gets written

For each registered sandbox, one TCP router + one TCP service:

```yaml
tcp:
  routers:
    sb-<sandbox-uuid>:
      entryPoints: [wss]
      rule: "HostSNI(`<sandbox-uuid>`)"
      service: sb-<sandbox-uuid>
      tls:
        certResolver: letsencrypt
        domains:
          - main: ssh.alcatraz.io      # GATEWAY_DOMAIN — the ACME-issued cert all sandboxes share
  services:
    sb-<sandbox-uuid>:
      loadBalancer:
        servers:
          - address: "172.16.0.10:22"  # worker-reported endpoint
```

All sandboxes share the same single ACME cert for `GATEWAY_DOMAIN`; routing is by SNI value, not by cert SAN. The `sb-` prefix on router/service names is cosmetic — it just keeps Traefik's dashboard sortable.

Writes are atomic (`tmp + rename`) and debounced (`DEBOUNCE_MS`, default 500 ms) so bursts of `vm.ready` events collapse into a single Traefik reload.

## Configuration

| Var | Default | Notes |
|---|---|---|
| `NATS_URL` | `nats://localhost:4222` | NATS server URL |
| `NATS_READY_SUBJECT` | `vm.ready` | Inbound: sandbox came up |
| `NATS_DESTROYED_SUBJECT` | `vm.destroyed` | Inbound: sandbox is gone |
| `OUTPUT_PATH` | `/etc/traefik/dynamic/sandboxes.yml` | Where Traefik's file provider watches |
| `GATEWAY_DOMAIN` | `ssh.alcatraz.io` | ACME hostname pinned on every router |
| `DEBOUNCE_MS` | `500` | Quiet window before flushing a write |

## Build

```bash
make build           # → bin/alcatraz-routes
go test ./...
```

The Dockerfile produces a static Alpine-based image; the repo-root [`docker-compose.yml`](../docker-compose.yml) builds and runs it under the `gateway` profile next to the Traefik service.

## Run

Inside compose (the canonical path):

```bash
GATEWAY_AUTOCERT_HOST=ssh.alcatraz.io \
docker compose --profile gateway up -d --build
```

Standalone (against a NATS reachable from your shell):

```bash
NATS_URL=nats://localhost:4222 \
OUTPUT_PATH=/tmp/sandboxes.yml \
GATEWAY_DOMAIN=ssh.alcatraz.io \
./bin/alcatraz-routes
```

It will write an empty `sandboxes.yml` immediately so Traefik can start cleanly even before any worker has published `vm.ready`. As events arrive, the file is rewritten atomically.

## Layout

```
alcatraz.routes/
├── cmd/alcatraz-routes/main.go     # wires config + NATS + registry + writer
├── internal/
│   ├── config/                     # env-driven config
│   ├── registry/                   # in-memory sandbox→endpoint, NATS consumer
│   └── writer/                     # debounced atomic Traefik YAML emitter (+ tests)
├── etc/traefik.yml                 # Traefik static config — mounted into the Traefik container
├── go.mod / Dockerfile / Makefile
```

`etc/traefik.yml` lives here (not in a separate `traefik/` dir) because the static Traefik config and the dynamic routes are owned together — they're both part of the gateway integration.

## Out of scope

- L7 routing or HTTP — this is pure TCP+SNI for SSH-over-TLS.
- Backend health checking — Traefik's dynamic config doesn't include health checks here; a stale route silently drops connections, and the next `vm.destroyed` removes it.
- Multi-host worker discovery — the worker reports whatever IP it has on its bridge subnet. In a multi-host deployment that bridge needs to be reachable from Traefik (BGP / WireGuard / VPC). Not solved here.
