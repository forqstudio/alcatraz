using System.Text.Json;
using Alcatraz.Application.Abstractions.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alcatraz.Infrastructure.Messaging;

internal sealed class NatsSandboxEventPublisher(
    NatsConnectionFactory connectionFactory,
    IOptions<NatsOptions> natsOptions,
    ILogger<NatsSandboxEventPublisher> logger
    ) : ISandboxEventPublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    private readonly NatsOptions natsOptions = natsOptions.Value;

    public async Task PublishSpawnAsync(
        Guid sandboxId,
        Guid ownerUserId,
        int vcpus,
        int memoryMib,
        CancellationToken cancellationToken = default)
    {
        var payload = new SpawnPayload(sandboxId.ToString(), vcpus, memoryMib, ownerUserId.ToString());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);

        await PublishAsync(natsOptions.SpawnSubject, bytes, cancellationToken);

        logger.LogInformation(
            "Published spawn request to NATS subject {Subject} for sandbox {SandboxId}",
            natsOptions.SpawnSubject,
            sandboxId);
    }

    public async Task PublishDestroyAsync(Guid sandboxId, CancellationToken cancellationToken = default)
    {
        var payload = new DestroyPayload(sandboxId.ToString());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);

        await PublishAsync(natsOptions.DestroySubject, bytes, cancellationToken);

        logger.LogInformation(
            "Published destroy request to NATS subject {Subject} for sandbox {SandboxId}",
            natsOptions.DestroySubject,
            sandboxId);
    }

    private async Task PublishAsync(string subject, byte[] payload, CancellationToken cancellationToken)
    {
        var connection = await connectionFactory.GetConnectionAsync(cancellationToken);
        await connection.PublishAsync(subject, payload, cancellationToken: cancellationToken);
    }

    private sealed record SpawnPayload(string Id, int Vcpus, int MemoryMib, string CustomerId);

    private sealed record DestroyPayload(string Id);
}
