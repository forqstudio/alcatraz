namespace Alcatraz.Application.Abstractions.Messaging;

public interface ISandboxEventPublisher
{
    Task PublishSpawnAsync(
        Guid sandboxId,
        Guid ownerUserId,
        int vcpus,
        int memoryMib,
        CancellationToken cancellationToken = default);

    Task PublishDestroyAsync(Guid sandboxId, CancellationToken cancellationToken = default);
}
