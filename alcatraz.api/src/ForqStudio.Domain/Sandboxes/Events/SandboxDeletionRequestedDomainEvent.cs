using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Sandboxes.Events;

public sealed record SandboxDeletionRequestedDomainEvent(Guid SandboxId) : IDomainEvent;
