# ADR-0010: Single root `docker-compose.yml` for local dev orchestration

- **Status:** Accepted
- **Date:** 2026-05-10
- **Related:** [`0011-devcontainer-scope.md`](0011-devcontainer-scope.md)

## Context

Earlier in the project, each component had its own `docker-compose.yml` (one per service directory). This split had two compounding failure modes:

1. **Overlapping infra services.** `alcatraz-nats`, `alcatraz-seq`, `alcatraz-redis` were declared in multiple compose files. Bringing two component stacks up at the same time produced "container name already in use" errors or worse — duplicate services on conflicting host ports.
2. **No single source of truth for the local stack.** A teammate trying to "just run everything" had to know which compose files to start, in which order, with which `--profile` flags. The README rotted as services moved.

The local-dev topology mirrors the deployment topology: one host, multiple cooperating services. Modelling that as N split compose files was an accidental complication.

## Decision

**One `docker-compose.yml` at the repo root.** Every dockerised service is declared there. Per-environment variation goes through Compose **profiles**, not separate files.

Services in the root compose:

- `alcatraz.api`, `alcatraz-db` (Postgres 16), `alcatraz-idp` (Keycloak 25, port 8082), `alcatraz-redis`, `alcatraz-nats`, `alcatraz-seq`, `alcatraz-ca-init` (one-shot), `alcatraz-demo-sshd` (stand-in VM for cert-pipeline tests, port 2222).
- Volumes: `keycloak_data`, `alcatraz_ca`. Image tags pinned to majors.

Excluded from compose (host-run, deliberate):

- `alcatraz.worker` — needs KVM + CNI + root, runs via `sudo -E ./bin/alcatraz-worker`.
- `alcatraz.cli` — built locally with `dotnet`.

Production-only services live under the `gateway` compose profile so local dev doesn't bring them up by default:

- `alcatraz-traefik` — `network_mode: host` so it can reach the worker's `alcatraz0` bridge (`172.16.0.0/24`).
- `alcatraz-routes` — writes `/etc/traefik/dynamic/sandboxes.yml` from `vm.ready` / `vm.destroyed` (see [ADR-0005](0005-nats-as-api-worker-coupling.md)).

EF migrations apply on API startup in Development.

## Consequences

### Positive

- **One source of truth.** `docker compose up` from the repo root brings up the local stack. No remembering which file to start.
- **No port conflicts.** Each infra container is declared exactly once.
- **Profiles model deployment shape.** Local dev = no profile; production-style ingress = `--profile gateway`. The same file describes both.
- **Matches deployment topology.** The local stack and the prod stack differ in which profile is active and which images are pinned, not in structure.

### Negative

- **Everything is in one file.** Editing the API's environment variables means scrolling past Postgres, Keycloak, Redis, etc. Acceptable today; if the file grows past ~300 lines we can split via Compose's `include:` (Compose 2.20+) without going back to N files.
- **Worker stays out.** Operators reading the compose file will not see the worker — that's deliberate (KVM/CNI/root means it can't be containerised the same way) but it's a teaching moment for new contributors who expect "everything in compose."
- **Profile choice is implicit.** `--profile gateway` is the only production-shaped variant; teammates have to know it exists. Documented in the README and in the compose file's comments.
