using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Sandboxes.RecordSandboxUsageSample;

public sealed record RecordSandboxUsageSampleCommand(
    Guid SandboxId,
    DateTime SampledAtUtc,
    long? CpuUsageUsecCumulative,
    long? NetRxBytesCumulative,
    long? NetTxBytesCumulative) : ICommand;
