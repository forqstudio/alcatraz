using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Sandboxes.Events;
using MediatR;

namespace Alcatraz.Application.Sandboxes.DeleteSandbox;

internal sealed class SandboxDeletionRequestedDomainHandler(
    ISandboxEventPublisher publisher
    ) : INotificationHandler<SandboxDeletionRequestedDomainEvent>
{
    public Task Handle(SandboxDeletionRequestedDomainEvent notification, CancellationToken cancellationToken) =>
        publisher.PublishDestroyAsync(notification.SandboxId, cancellationToken);
}
