using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Sandboxes.Events;
using MediatR;

namespace Alcatraz.Application.Sandboxes.CreateSandbox;

internal sealed class SandboxRequestedDomainHandler(
    ISandboxEventPublisher publisher
    ) : INotificationHandler<SandboxRequestedDomainEvent>
{
    public Task Handle(SandboxRequestedDomainEvent notification, CancellationToken cancellationToken) =>
        publisher.PublishSpawnAsync(
            notification.SandboxId,
            notification.OwnerUserId,
            notification.Vcpus,
            notification.MemoryMib,
            cancellationToken);
}
