using System.Text.Json;
using System.Text.Json.Serialization;
using Alcatraz.Application.Sandboxes.MarkSandboxDestroyed;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alcatraz.Infrastructure.Messaging;

internal sealed class VmDestroyedConsumer(
    NatsConnectionFactory connectionFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<NatsOptions> natsOptions,
    ILogger<VmDestroyedConsumer> logger
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
            natsOptions.DestroyedSubject,
            natsOptions.DestroyedQueueGroup);

        await foreach (var msg in connection.SubscribeAsync<byte[]>(
            natsOptions.DestroyedSubject,
            queueGroup: natsOptions.DestroyedQueueGroup,
            cancellationToken: stoppingToken))
        {
            try
            {
                if (msg.Data is null || msg.Data.Length == 0)
                {
                    logger.LogWarning("vm.destroyed: empty payload, skipping");
                    continue;
                }

                var payload = JsonSerializer.Deserialize<VmDestroyedPayload>(msg.Data, JsonOptions);
                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.Id) ||
                    !Guid.TryParse(payload.Id, out var sandboxId))
                {
                    logger.LogWarning("vm.destroyed: malformed payload — skipping");
                    continue;
                }

                using var scope = scopeFactory.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();

                var result = await sender.Send(
                    new MarkSandboxDestroyedCommand(sandboxId),
                    stoppingToken);

                if (result.IsFailure)
                {
                    logger.LogWarning(
                        "vm.destroyed handler failed for sandbox {SandboxId}: {Error}",
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
                logger.LogError(ex, "vm.destroyed: unhandled exception while processing message");
            }
        }
    }

    private sealed record VmDestroyedPayload(
        [property: JsonPropertyName("id")] string Id);
}
