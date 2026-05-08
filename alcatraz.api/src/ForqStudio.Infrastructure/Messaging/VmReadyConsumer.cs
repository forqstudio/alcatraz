using System.Text.Json;
using System.Text.Json.Serialization;
using ForqStudio.Application.Sandboxes.MarkSandboxRunning;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForqStudio.Infrastructure.Messaging;

internal sealed class VmReadyConsumer(
    NatsConnectionFactory connectionFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<NatsOptions> natsOptions,
    ILogger<VmReadyConsumer> logger
    ) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly NatsOptions natsOptions = natsOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = await connectionFactory.GetConnectionAsync(stoppingToken);

        logger.LogInformation(
            "Subscribing to NATS subject {Subject} (queue group {QueueGroup})",
            natsOptions.ReadySubject,
            natsOptions.ReadyQueueGroup);

        await foreach (var msg in connection.SubscribeAsync<byte[]>(
            natsOptions.ReadySubject,
            queueGroup: natsOptions.ReadyQueueGroup,
            cancellationToken: stoppingToken))
        {
            try
            {
                if (msg.Data is null || msg.Data.Length == 0)
                {
                    logger.LogWarning("vm.ready: empty payload, skipping");
                    continue;
                }

                var payload = JsonSerializer.Deserialize<VmReadyPayload>(msg.Data, JsonOptions);
                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.Id) ||
                    !Guid.TryParse(payload.Id, out var sandboxId) ||
                    string.IsNullOrWhiteSpace(payload.Host) ||
                    payload.Port <= 0)
                {
                    logger.LogWarning("vm.ready: malformed payload — skipping");
                    continue;
                }

                using var scope = scopeFactory.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();

                var result = await sender.Send(
                    new MarkSandboxRunningCommand(sandboxId, payload.Host, payload.Port),
                    stoppingToken);

                if (result.IsFailure)
                {
                    logger.LogWarning(
                        "vm.ready handler failed for sandbox {SandboxId}: {Error}",
                        sandboxId,
                        result.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "vm.ready: unhandled exception while processing message");
            }
        }
    }

    private sealed record VmReadyPayload(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("host")] string Host,
        [property: JsonPropertyName("port")] int Port);
}
