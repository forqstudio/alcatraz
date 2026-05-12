using System.Text.Json;
using Alcatraz.Application.Sandboxes.RecordSandboxUsageSample;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;

namespace Alcatraz.Infrastructure.Messaging;

internal sealed class VmUsageSampleConsumer(
    NatsConnectionFactory connectionFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<NatsOptions> natsOptions,
    ILogger<VmUsageSampleConsumer> logger
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
            natsOptions.UsageSampleConsumerName,
            stoppingToken);

        logger.LogInformation(
            "Consuming JetStream {Stream}/{Consumer} ({Subject})",
            natsOptions.UsageStreamName,
            natsOptions.UsageSampleConsumerName,
            natsOptions.UsageSampleSubject);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: stoppingToken))
        {
            try
            {
                if (msg.Data is null || msg.Data.Length == 0)
                {
                    logger.LogWarning("vm.usage_sample: empty payload — acking and skipping");
                    await msg.AckAsync(cancellationToken: stoppingToken);
                    continue;
                }

                var payload = JsonSerializer.Deserialize<UsageSamplePayload>(msg.Data, JsonOptions);
                if (payload is null ||
                    !Guid.TryParse(payload.SandboxId, out var sandboxId))
                {
                    logger.LogWarning("vm.usage_sample: malformed payload — acking and skipping");
                    await msg.AckAsync(cancellationToken: stoppingToken);
                    continue;
                }

                using var scope = scopeFactory.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();

                var result = await sender.Send(
                    new RecordSandboxUsageSampleCommand(
                        sandboxId,
                        payload.SampledAtUtc,
                        payload.CpuUsageUsecCumulative,
                        payload.NetRxBytesCumulative,
                        payload.NetTxBytesCumulative),
                    stoppingToken);

                if (result.IsFailure)
                {
                    logger.LogWarning(
                        "vm.usage_sample handler failed for sandbox {SandboxId}: {Error}",
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
                logger.LogError(ex, "vm.usage_sample: unhandled exception, naking");
                try
                {
                    await msg.NakAsync(cancellationToken: stoppingToken);
                }
                catch (Exception nakEx)
                {
                    logger.LogError(nakEx, "vm.usage_sample: nak failed");
                }
            }
        }
    }

    private sealed record UsageSamplePayload(
        string SandboxId,
        DateTime SampledAtUtc,
        long? CpuUsageUsecCumulative,
        long? NetRxBytesCumulative,
        long? NetTxBytesCumulative);
}
