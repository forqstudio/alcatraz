# ADR-0004: Customer SSH access — SSH CA + per-sandbox principals + SNI gateway

- **Status:** Accepted
- **Date:** 2026-05-10
- **Related:** [`0006-keycloak-identity-provider.md`](0006-keycloak-identity-provider.md), [`0007-agentfs-overlay-pre-boot.md`](0007-agentfs-overlay-pre-boot.md), [`0008-rootfs-init-tini-sshd.md`](0008-rootfs-init-tini-sshd.md), [`../../plans/architecture.md`](../../plans/architecture.md)

## Context

Alcatraz is a paid, multi-tenant serverless sandbox. The customer is **external** — a paying user, not a teammate. Four hard constraints from the architecture overview drive the entire customer-facing surface:

1. Each customer sees only their own VM. From the customer's perspective the worker host does not exist.
2. Customer ↔ customer isolation must hold at network, transport, *and* discovery layers — even on a misconfigured network.
3. No long-term storage of customer SSH pubkeys.
4. Frictionless client: customers use stock `ssh` — no WireGuard install, no proprietary client.
5. (And constraint 6) Failure isolation: no single component compromise yields VM access.

A naive design — register a customer's pubkey in `authorized_keys` on the VM, expose the worker's IP, route by hostname — fails 1, 2, 3, and 6 simultaneously: pubkeys are persisted, worker IPs leak, a compromised gateway hands an attacker SSH, a misrouted packet reaches the wrong VM.

## Decision

Three reinforcing mechanisms:

1. **SSH CA with short-lived per-sandbox certificates.**
   - `alcatraz.api` operates an SSH CA. The private key never leaves the API; the public key is distributed (baked into the rootfs at build time and written into each VM's overlay at spawn).
   - Per SSH session, the CLI generates an ed25519 workstation keypair on first use, POSTs `{sshPubkey}` + bearer token to `/api/v1/sandboxes/{id}/ssh-cert`, and gets back an OpenSSH user cert with `principal = <sandbox-id>`, `valid_before = now+24h`, `key_id = <identityId>:<sandboxId>:<unixTs>`.
   - The pubkey is **never persisted** by the API. Cert signing shells out to `ssh-keygen -s` (`openssh-client` in the API container).

2. **Per-VM principals file as the cryptographic per-tenant scope.**
   - The VM's sshd has `TrustedUserCAKeys /etc/ssh/trusted_user_ca_keys` and `AuthorizedPrincipalsFile /etc/ssh/auth_principals/%u`.
   - The worker writes a single line — the sandbox UUID — into `auth_principals/al` inside the AgentFS overlay before boot (see [ADR-0007](0007-agentfs-overlay-pre-boot.md)). sshd accepts only certs whose principal matches that one UUID.
   - SSH user is always `al`; principal scoping happens via `auth_principals`, not via the username.

3. **SNI-as-routing-key gateway.**
   - Public ingress (`gateway` profile): single TLS endpoint `ssh.alcatraz.io:443`. Traefik terminates TLS, matches `SNI = sandbox UUID` against a per-sandbox TCP router, splices bytes to `<vm_ip>:22`.
   - CLI invokes stock `ssh` with `ProxyCommand="openssl s_client -quiet -connect ssh.alcatraz.io:443 -servername <id>"`.
   - ACME via TLS-ALPN-01 (single cert for `ssh.alcatraz.io`, shared across routers).
   - Local dev (no `gateway` profile): `Gateway:Host` is unset, so the cert response carries the per-sandbox VM endpoint (`172.16.0.x:22`) and the CLI dials directly. **Same code path, one config flip.**

## Consequences

### Positive

- **Cryptographic per-tenant isolation that survives a compromised gateway or API.** Even if the gateway misroutes a connection, the VM's sshd independently verifies the cert against its baked-in CA pubkey + the per-VM principals file. Wrong principal = rejected at the VM.
- **No persisted pubkey registry.** Constraint 3 is satisfied at the data-model level — there's no table to leak.
- **Stock `ssh` on the client.** Constraint 4 is satisfied with `ProxyCommand` and `openssl s_client`, both standard tools.
- **Worker IPs never leak to the customer.** SNI hides the topology behind a single public endpoint; the customer never resolves a worker hostname.
- **Cert TTL is the revocation primitive.** No KRL needed yet; 24h limits the blast radius of a leaked cert.

### Negative

- **24h is the floor for revocation.** Until KRL is shipped, a compromised cert remains valid until expiry. Listed in `plans/open-issues.md`.
- **CA private key is a high-value secret living in the API container.** A compromised API container yields cert-issuance capability across all sandboxes. Mitigations rely on container hardening; HSM-backed signing is open work.
- **CA rotation is unrehearsed.** A dual-CA window approach is documented but not exercised. First rotation is a runbook gap.
- **TLS-ALPN-01 needs port 443 reachable.** Production ingress is single-host today; multi-host distribution of the ACME challenge is open work.
- **VM subnet is hardcoded.** `172.16.0.0/24` per worker (`cni/alcatraz-bridge.conflist`). Single-host only — multi-host needs per-host /24 carving.

### Locked-in parameters

- **Cert TTL:** 24h.
- **Cert principal:** per-sandbox UUID. Username is always `al`.
- **CA algorithm:** Ed25519.
- **SSH gateway port:** 443 (shared with HTTPS-style TLS termination).
