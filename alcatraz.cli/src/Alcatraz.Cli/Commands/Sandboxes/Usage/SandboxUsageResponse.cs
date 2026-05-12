namespace Alcatraz.Cli.Commands.Sandboxes.Usage;

public sealed class SandboxUsageResponse
{
    public Guid SandboxId { get; init; }

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
