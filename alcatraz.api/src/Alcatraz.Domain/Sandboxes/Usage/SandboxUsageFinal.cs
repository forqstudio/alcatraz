namespace Alcatraz.Domain.Sandboxes.Usage;

// Snapshot of cumulative usage counters reported by the worker when a sandbox
// VM exits. Cumulative since VM boot. Nullable fields mean the underlying
// source (cgroup, Firecracker metrics file) was unreadable for that dimension.
public sealed record SandboxUsageFinal(
    DateTime VmBootedAtUtc,
    DateTime FinalisedAtUtc,
    long? TotalCpuUsageUsec,
    long? TotalNetRxBytes,
    long? TotalNetTxBytes,
    int SampleCount);
