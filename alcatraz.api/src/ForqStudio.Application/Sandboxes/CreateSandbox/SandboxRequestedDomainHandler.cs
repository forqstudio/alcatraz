using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Sandboxes.Events;
using MediatR;

namespace ForqStudio.Application.Sandboxes.CreateSandbox;

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
