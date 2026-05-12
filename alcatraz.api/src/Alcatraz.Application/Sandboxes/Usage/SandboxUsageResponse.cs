namespace Alcatraz.Application.Sandboxes.Usage;

public sealed class SandboxUsageResponse
{
    public Guid SandboxId { get; init; }

    public Guid OwnerUserId { get; init; }

    // True once the worker has published vm.usage_final and the record is
    // immutable. False while the sandbox is still running — totals in that
    // case are computed from the latest sample and the current clock.
    public bool Finalised { get; init; }

    public DateTime BillingWindowStartUtc { get; init; }

    public DateTime BillingWindowEndUtc { get; init; }

    public long ProvisionedVcpuSeconds { get; init; }

    public long ProvisionedMemoryMibSeconds { get; init; }

    public long? ActualCpuUsageUsec { get; init; }

    public long? ActualNetRxBytes { get; init; }

    public long? ActualNetTxBytes { get; init; }

    public int SampleCount { get; init; }

    public DateTime? FinalisedAtUtc { get; init; }
}
