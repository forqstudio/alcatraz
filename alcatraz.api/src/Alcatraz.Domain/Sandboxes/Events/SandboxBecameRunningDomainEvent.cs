using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Domain.Sandboxes.Events;

public sealed record SandboxBecameRunningDomainEvent(
    Guid SandboxId,
    string Host,
    int Port,
    int ActualVcpus,
    int ActualMemoryMib,
    int BootDurationMs,
    DateTime ReadyAtUtc) : IDomainEvent;
