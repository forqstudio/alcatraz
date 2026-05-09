using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Domain.Sandboxes.Events;

public sealed record SandboxDeletionRequestedDomainEvent(Guid SandboxId) : IDomainEvent;
