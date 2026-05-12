using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Domain.Sandboxes.Events;

public sealed record SandboxUsageRecordedDomainEvent(Guid SandboxId) : IDomainEvent;
