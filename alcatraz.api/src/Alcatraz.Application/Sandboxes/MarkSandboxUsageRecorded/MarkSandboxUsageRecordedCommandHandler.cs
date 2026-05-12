using Alcatraz.Application.Abstractions.Clock;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Sandboxes.Usage;
using Microsoft.Extensions.Logging;

namespace Alcatraz.Application.Sandboxes.MarkSandboxUsageRecorded;

internal sealed class MarkSandboxUsageRecordedCommandHandler(
    ISandboxRepository sandboxRepository,
    ISandboxUsageRecordRepository usageRecordRepository,
    IUnitOfWork unitOfWork,
    IDateTimeProvider dateTimeProvider,
    ILogger<MarkSandboxUsageRecordedCommandHandler> logger
    ) : ICommandHandler<MarkSandboxUsageRecordedCommand>
{
    public async Task<Result> Handle(MarkSandboxUsageRecordedCommand request, CancellationToken cancellationToken)
    {
        var existing = await usageRecordRepository.GetBySandboxIdAsync(request.SandboxId, cancellationToken);
        if (existing is not null)
        {
            return Result.Success();
        }

        var sandbox = await sandboxRepository.GetByIdAsync(request.SandboxId, cancellationToken);
        if (sandbox is null)
        {
            logger.LogWarning(
                "vm.usage_final for unknown sandbox {SandboxId} — ignoring",
                request.SandboxId);
            return Result.Failure(SandboxUsageErrors.SandboxNotFound);
        }

        var finalise = SandboxUsageRecord.Finalise(sandbox, request.Final, dateTimeProvider.UtcNow);
        if (finalise.IsFailure)
        {
            logger.LogWarning(
                "Could not finalise usage for sandbox {SandboxId}: {Error}",
                request.SandboxId,
                finalise.Error);
            return Result.Failure(finalise.Error);
        }

        usageRecordRepository.Add(finalise.Value);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Sandbox {SandboxId} usage recorded: provisioned vcpu_s={VcpuS} mem_mib_s={MemMibS}, actual cpu_usec={Cpu} rx_bytes={Rx} tx_bytes={Tx}, samples={Samples}",
            request.SandboxId,
            finalise.Value.ProvisionedVcpuSeconds,
            finalise.Value.ProvisionedMemoryMibSeconds,
            finalise.Value.ActualCpuUsageUsec,
            finalise.Value.ActualNetRxBytes,
            finalise.Value.ActualNetTxBytes,
            finalise.Value.SampleCount);

        return Result.Success();
    }
}
