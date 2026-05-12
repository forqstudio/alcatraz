using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Domain.Sandboxes.Usage;

// One cumulative-counter snapshot from the worker. Append-only audit row;
// has no business behaviour. Uniqueness on (SandboxId, SampledAtUtc) gives
// idempotency against JetStream redelivery.
public sealed class SandboxUsageSample : Entity
{
    private SandboxUsageSample(
        Guid id,
        Guid sandboxId,
        DateTime sampledAtUtc,
        long? cpuUsageUsecCumulative,
        long? netRxBytesCumulative,
        long? netTxBytesCumulative)
        : base(id)
    {
        SandboxId = sandboxId;
        SampledAtUtc = sampledAtUtc;
        CpuUsageUsecCumulative = cpuUsageUsecCumulative;
        NetRxBytesCumulative = netRxBytesCumulative;
        NetTxBytesCumulative = netTxBytesCumulative;
    }

    private SandboxUsageSample() { }

    public Guid SandboxId { get; private set; }

    public DateTime SampledAtUtc { get; private set; }

    public long? CpuUsageUsecCumulative { get; private set; }

    public long? NetRxBytesCumulative { get; private set; }

    public long? NetTxBytesCumulative { get; private set; }

    public static SandboxUsageSample Record(
        Guid sandboxId,
        DateTime sampledAtUtc,
        long? cpuUsageUsecCumulative,
        long? netRxBytesCumulative,
        long? netTxBytesCumulative) =>
        new(
            Guid.NewGuid(),
            sandboxId,
            sampledAtUtc,
            cpuUsageUsecCumulative,
            netRxBytesCumulative,
            netTxBytesCumulative);
}
