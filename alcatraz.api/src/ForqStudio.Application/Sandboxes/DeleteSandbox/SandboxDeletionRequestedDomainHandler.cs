using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Sandboxes.Events;
using MediatR;

namespace ForqStudio.Application.Sandboxes.DeleteSandbox;

internal sealed class SandboxDeletionRequestedDomainHandler(
    ISandboxEventPublisher publisher
    ) : INotificationHandler<SandboxDeletionRequestedDomainEvent>
{
    public Task Handle(SandboxDeletionRequestedDomainEvent notification, CancellationToken cancellationToken) =>
        publisher.PublishDestroyAsync(notification.SandboxId, cancellationToken);
}
