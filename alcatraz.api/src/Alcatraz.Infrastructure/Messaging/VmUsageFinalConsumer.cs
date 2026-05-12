using System.Text.Json;
using Alcatraz.Application.Sandboxes.MarkSandboxUsageRecorded;
using Alcatraz.Domain.Sandboxes.Usage;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;

namespace Alcatraz.Infrastructure.Messaging;

internal sealed class VmUsageFinalConsumer(
    NatsConnectionFactory connectionFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<NatsOptions> natsOptions,
    ILogger<VmUsageFinalConsumer> logger
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
        var js = new NatsJSContext(connection);
        var consumer = await js.GetConsumerAsync(
            natsOptions.UsageStreamName,
            natsOptions.UsageFinalConsumerName,
            stoppingToken);

        logger.LogInformation(
            "Consuming JetStream {Stream}/{Consumer} ({Subject})",
            natsOptions.UsageStreamName,
            natsOptions.UsageFinalConsumerName,
            natsOptions.UsageFinalSubject);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: stoppingToken))
        {
            try
            {
                if (msg.Data is null || msg.Data.Length == 0)
                {
                    logger.LogWarning("vm.usage_final: empty payload — acking and skipping");
                    await msg.AckAsync(cancellationToken: stoppingToken);
                    continue;
                }

                var payload = JsonSerializer.Deserialize<UsageFinalPayload>(msg.Data, JsonOptions);
                if (payload is null ||
                    !Guid.TryParse(payload.SandboxId, out var sandboxId))
                {
                    logger.LogWarning("vm.usage_final: malformed payload — acking and skipping");
                    await msg.AckAsync(cancellationToken: stoppingToken);
                    continue;
                }

                var final = new SandboxUsageFinal(
                    payload.VmBootedAtUtc,
                    payload.FinalisedAtUtc,
                    payload.TotalCpuUsageUsec,
                    payload.TotalNetRxBytes,
                    payload.TotalNetTxBytes,
                    payload.SampleCount);

                using var scope = scopeFactory.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();

                var result = await sender.Send(
                    new MarkSandboxUsageRecordedCommand(sandboxId, final),
                    stoppingToken);

                if (result.IsFailure)
                {
                    logger.LogWarning(
                        "vm.usage_final handler failed for sandbox {SandboxId}: {Error}",
                        sandboxId,
                        result.Error);
                    await msg.NakAsync(cancellationToken: stoppingToken);
                    continue;
                }

                await msg.AckAsync(cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "vm.usage_final: unhandled exception, naking");
                try
                {
                    await msg.NakAsync(cancellationToken: stoppingToken);
                }
                catch (Exception nakEx)
                {
                    logger.LogError(nakEx, "vm.usage_final: nak failed");
                }
            }
        }
    }

    private sealed record UsageFinalPayload(
        string SandboxId,
        DateTime VmBootedAtUtc,
        DateTime FinalisedAtUtc,
        long? TotalCpuUsageUsec,
        long? TotalNetRxBytes,
        long? TotalNetTxBytes,
        int SampleCount);
}
