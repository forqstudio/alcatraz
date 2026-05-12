using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes.Usage;

namespace Alcatraz.Application.Sandboxes.RecordSandboxUsageSample;

internal sealed class RecordSandboxUsageSampleCommandHandler(
    ISandboxUsageSampleRepository sampleRepository,
    IUnitOfWork unitOfWork
    ) : ICommandHandler<RecordSandboxUsageSampleCommand>
{
    public async Task<Result> Handle(RecordSandboxUsageSampleCommand request, CancellationToken cancellationToken)
    {
        // Idempotency: redelivered samples are deduped by the unique
        // (sandbox_id, sampled_at_utc) index. JetStream redelivery is
        // sequential per-consumer, so check-then-insert is race-free here.
        if (await sampleRepository.ExistsAsync(request.SandboxId, request.SampledAtUtc, cancellationToken))
        {
            return Result.Success();
        }

        var sample = SandboxUsageSample.Record(
            request.SandboxId,
            request.SampledAtUtc,
            request.CpuUsageUsecCumulative,
            request.NetRxBytesCumulative,
            request.NetTxBytesCumulative);

        sampleRepository.Add(sample);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
