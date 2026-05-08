using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Sandboxes.Events;

public sealed record SandboxBecameRunningDomainEvent(
    Guid SandboxId,
    string Host,
    int Port) : IDomainEvent;
