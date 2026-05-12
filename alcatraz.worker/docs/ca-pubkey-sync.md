# CA pubkey sync — why and how

The worker embeds the API's SSH CA *public* key on every VM's kernel cmdline (base64-encoded) at spawn time; the guest's `/init` materialises it in tmpfs at `/run/ssh-config/trusted_user_ca_keys` before sshd starts. The sync script (`alcatraz.worker/scripts/sync-ca-pubkey.sh`) copies that pubkey out of the `alcatraz_ca` Docker volume to `/run/alcatraz-ca/alcatraz_ca.pub` on the host, where the worker reads it once at startup. This doc explains the design choices behind that pipeline.

## Why the worker needs the pubkey at all

`alcatraz.api` is the SSH certificate authority. It holds an ed25519 *private* key (mounted from the `alcatraz_ca` Docker volume) and uses it to sign 24-hour OpenSSH user certificates the CLI presents to a sandbox's `sshd`.

For `sshd` to trust those certs, every VM needs the matching *public* key planted at `/run/ssh-config/trusted_user_ca_keys` before boot. The worker is the component that puts that file in place — by appending the pubkey (base64) to the Firecracker kernel cmdline as `alcatraz.ca_pubkey=…`, which the guest's `/init` decodes into tmpfs. So the worker, not the API, is the one that needs the pubkey on disk.

## Why a host-side sync (and not a shared volume)

The worker runs on the host, outside compose, because it needs KVM and CNI. That puts it outside the Docker network and outside reach of the `alcatraz_ca` volume. `sync-ca-pubkey.sh` is the bridge: it pulls just the public half out of the volume to a path the host worker can read.

Only the public key crosses this boundary. The private key never leaves the API container.

## Why Keycloak isn't the SSH CA

Two different authentications, two different mechanisms:

- **Keycloak** authenticates the *human*. It issues JWTs the CLI presents to the API.
- **The API** authorises the *SSH connection*. It issues OpenSSH user certs the CLI presents to `sshd`.

`sshd` doesn't speak OAuth. It only validates SSH certs against a trusted CA pubkey — which is what the API holds and what this sync makes available to the worker.

## Why `/run` (and what it costs you)

`/run` is the Linux convention for *runtime* state — files programs need while running but should never persist across a reboot. On modern Linuxes it's mounted as `tmpfs`, a RAM-backed filesystem, so contents are wiped at boot and never hit disk.

That's a good fit here:

- The pubkey is cheap to re-derive from the Docker volume.
- There's no stale on-disk copy to forget about during CA rotation.
- A reboot can't leave behind a pubkey that no longer matches the API's private key.

The cost: **you must re-run `sync-ca-pubkey.sh` after every host reboot.** The worker refuses to start without it.

## Why the *worker* fails fast (not the API)

The API doesn't read the public key — it holds the *private* key and signs certs with it. If the API's private key is missing, the failure is loud and obvious on the first cert request.

The worker is the only process whose job depends on the *public* key existing on the host. If the worker started without it, every spawn would still *succeed* end-to-end:

1. VM boots.
2. `vm.ready` fires.
3. The API flips the sandbox to `Running`.
4. The API issues a cert when the customer asks for one.
5. Only the customer's first SSH attempt fails — silently, with a generic `Permission denied (publickey)`.

That's the worst-case failure mode: looks healthy, isn't, customer hits it last. To prevent it, the worker reads the file once at startup and refuses to start if it's missing or unreadable (`alcatraz.worker/cmd/alcatraz-worker/main.go:52`). The pubkey bytes are held in memory and embedded into every spawn's kernel cmdline — no per-spawn re-read, no per-spawn failure surface.

The startup failure surfaces immediately to the operator, not silently to the customer.
