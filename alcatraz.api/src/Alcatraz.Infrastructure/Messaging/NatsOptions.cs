namespace Alcatraz.Infrastructure.Messaging;

public sealed class NatsOptions
{
    public string Url { get; set; } = "nats://localhost:4222";

    public string SpawnSubject { get; set; } = "vm.spawn";

    public string DestroySubject { get; set; } = "vm.destroy";

    public string ReadySubject { get; set; } = "vm.ready";

    public string ReadyQueueGroup { get; set; } = "api-vm-ready";

    public string DestroyedSubject { get; set; } = "vm.destroyed";

    public string DestroyedQueueGroup { get; set; } = "api-vm-destroyed";

    public string UsageSampleSubject { get; set; } = "vm.usage_sample";

    public string UsageFinalSubject { get; set; } = "vm.usage_final";

    public string UsageStreamName { get; set; } = "ALCATRAZ_USAGE";

    public string UsageSampleConsumerName { get; set; } = "usage-sample-consumer";

    public string UsageFinalConsumerName { get; set; } = "usage-final-consumer";
}
