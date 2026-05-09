using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Domain.Sandboxes.Events;

public sealed record SandboxRequestedDomainEvent(
    Guid SandboxId,
    Guid OwnerUserId,
    int Vcpus,
    int MemoryMib) : IDomainEvent;
