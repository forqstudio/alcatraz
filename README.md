# Alcatraz

A sanboxed environment for letting your coding agents run wild.

## Overview

Alcatraz spins up ephemeral Firecracker microVMs on demand. Each VM backs its root filesystem with AgentFS overlay on NFS, so agent changes persist across restarts while keeping the base image clean. All filesystem operations and tools calls are audited by default. 

## Components

| Component | Description |
|-----------|-------------|
| [alcatraz.core](alcatraz.core/) | Firecracker microVM with AgentFS overlay |
| [alcatraz.worker](alcatraz.worker/) | NATS-driven dynamic VM spawner |
| alcatraz.api | Stateless API for auth + VM coordination ([TODO](#alcatrazapi)) |
| alcatraz.cli | User Interface ([TODO](#alcatrazcli)) |

### alcatraz.core

See [alcatraz.core/README.md](alcatraz.core/README.md) for details.

### alcatraz.worker

See [alcatraz.worker/README.md](alcatraz.worker/README.md) for details.

### alcatraz.api TODO


### alcatraz.cli TODO
```
