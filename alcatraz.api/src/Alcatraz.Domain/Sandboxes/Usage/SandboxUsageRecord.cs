using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes.Events;

namespace Alcatraz.Domain.Sandboxes.Usage;

public sealed class SandboxUsageRecord : Entity
{
    private SandboxUsageRecord(
        Guid sandboxId,
        DateTime billingWindowStartUtc,
        DateTime billingWindowEndUtc,
        long provisionedVcpuSeconds,
        long provisionedMemoryMibSeconds,
        long? actualCpuUsageUsec,
        long? actualNetRxBytes,
        long? actualNetTxBytes,
        int sampleCount,
        DateTime finalisedAtUtc)
        : base(sandboxId)
    {
        BillingWindowStartUtc = billingWindowStartUtc;
        BillingWindowEndUtc = billingWindowEndUtc;
        ProvisionedVcpuSeconds = provisionedVcpuSeconds;
        ProvisionedMemoryMibSeconds = provisionedMemoryMibSeconds;
        ActualCpuUsageUsec = actualCpuUsageUsec;
        ActualNetRxBytes = actualNetRxBytes;
        ActualNetTxBytes = actualNetTxBytes;
        SampleCount = sampleCount;
        FinalisedAtUtc = finalisedAtUtc;
    }

    private SandboxUsageRecord() { }

    public Guid SandboxId => Id;

    public DateTime BillingWindowStartUtc { get; private set; }

    public DateTime BillingWindowEndUtc { get; private set; }

    public long ProvisionedVcpuSeconds { get; private set; }

    public long ProvisionedMemoryMibSeconds { get; private set; }

    public long? ActualCpuUsageUsec { get; private set; }

    public long? ActualNetRxBytes { get; private set; }

    public long? ActualNetTxBytes { get; private set; }

    public int SampleCount { get; private set; }

    public DateTime FinalisedAtUtc { get; private set; }

    public static Result<SandboxUsageRecord> Finalise(
        Sandbox sandbox,
        SandboxUsageFinal final,
        DateTime utcNow)
    {
        if (sandbox.ReadyAtUtc is null || sandbox.ActualVcpus is null || sandbox.ActualMemoryMib is null)
        {
            return Result.Failure<SandboxUsageRecord>(SandboxUsageErrors.SandboxNotFinalisable);
        }

        var windowStart = sandbox.ReadyAtUtc.Value;
        var windowEnd = sandbox.DeletedOnUtc ?? final.FinalisedAtUtc;
        if (windowEnd < windowStart)
        {
            windowEnd = windowStart;
        }

        var windowSeconds = (long)Math.Max(0, (windowEnd - windowStart).TotalSeconds);

        var record = new SandboxUsageRecord(
            sandbox.Id,
            windowStart,
            windowEnd,
            checked(sandbox.ActualVcpus.Value * windowSeconds),
            checked(sandbox.ActualMemoryMib.Value * windowSeconds),
            final.TotalCpuUsageUsec,
            final.TotalNetRxBytes,
            final.TotalNetTxBytes,
            final.SampleCount,
            utcNow);

        record.RaiseDomainEvent(new SandboxUsageRecordedDomainEvent(sandbox.Id));
        return Result.Success(record);
    }
}
