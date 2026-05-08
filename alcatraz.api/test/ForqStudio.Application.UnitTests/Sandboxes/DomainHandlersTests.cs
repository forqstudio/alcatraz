using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Application.Sandboxes.CreateSandbox;
using ForqStudio.Application.Sandboxes.DeleteSandbox;
using ForqStudio.Domain.Sandboxes.Events;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Sandboxes;

public class DomainHandlersTests
{
    [Fact]
    public async Task SandboxRequestedDomainHandler_InvokesPublishSpawn()
    {
        var publisher = Substitute.For<ISandboxEventPublisher>();
        var handler = new SandboxRequestedDomainHandler(publisher);

        var sandboxId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        await handler.Handle(new SandboxRequestedDomainEvent(sandboxId, ownerId, 4, 4096), CancellationToken.None);

        await publisher.Received(1).PublishSpawnAsync(sandboxId, ownerId, 4, 4096, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SandboxDeletionRequestedDomainHandler_InvokesPublishDestroy()
    {
        var publisher = Substitute.For<ISandboxEventPublisher>();
        var handler = new SandboxDeletionRequestedDomainHandler(publisher);

        var sandboxId = Guid.NewGuid();

        await handler.Handle(new SandboxDeletionRequestedDomainEvent(sandboxId), CancellationToken.None);

        await publisher.Received(1).PublishDestroyAsync(sandboxId, Arg.Any<CancellationToken>());
    }
}
