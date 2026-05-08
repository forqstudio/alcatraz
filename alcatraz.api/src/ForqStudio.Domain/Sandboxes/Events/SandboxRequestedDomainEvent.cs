using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Sandboxes.Events;

public sealed record SandboxRequestedDomainEvent(
    Guid SandboxId,
    Guid OwnerUserId,
    int Vcpus,
    int MemoryMib) : IDomainEvent;
