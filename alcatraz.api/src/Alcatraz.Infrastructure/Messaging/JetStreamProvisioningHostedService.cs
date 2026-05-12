using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Alcatraz.Infrastructure.Messaging;

// Idempotently declares the JetStream stream and durable consumers that the
// usage-metering pipeline depends on. Runs once at API startup, before the
// usage consumers begin fetching.
internal sealed class JetStreamProvisioningHostedService(
    NatsConnectionFactory connectionFactory,
    IOptions<NatsOptions> natsOptions,
    ILogger<JetStreamProvisioningHostedService> logger)
    : IHostedService
{
    private readonly NatsOptions natsOptions = natsOptions.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var connection = await connectionFactory.GetConnectionAsync(cancellationToken);
        var js = new NatsJSContext(connection);

        await js.CreateOrUpdateStreamAsync(new StreamConfig
        {
            Name = natsOptions.UsageStreamName,
            Subjects = new[] { natsOptions.UsageSampleSubject, natsOptions.UsageFinalSubject },
            Storage = StreamConfigStorage.File,
            Retention = StreamConfigRetention.Interest,
            Discard = StreamConfigDiscard.Old,
            MaxMsgSize = 64 * 1024,
            NumReplicas = 1,
        }, cancellationToken);

        await js.CreateOrUpdateConsumerAsync(
            natsOptions.UsageStreamName,
            new ConsumerConfig(natsOptions.UsageSampleConsumerName)
            {
                FilterSubject = natsOptions.UsageSampleSubject,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                AckWait = TimeSpan.FromSeconds(30),
                MaxDeliver = 5,
            },
            cancellationToken);

        await js.CreateOrUpdateConsumerAsync(
            natsOptions.UsageStreamName,
            new ConsumerConfig(natsOptions.UsageFinalConsumerName)
            {
                FilterSubject = natsOptions.UsageFinalSubject,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                AckWait = TimeSpan.FromSeconds(60),
                MaxDeliver = 10,
            },
            cancellationToken);

        logger.LogInformation(
            "JetStream stream {Stream} ready with consumers {SampleConsumer} and {FinalConsumer}",
            natsOptions.UsageStreamName,
            natsOptions.UsageSampleConsumerName,
            natsOptions.UsageFinalConsumerName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
