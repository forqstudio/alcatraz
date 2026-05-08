using ForqStudio.Application.Abstractions.Clock;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Sandboxes;
using Microsoft.Extensions.Logging;

namespace ForqStudio.Application.Sandboxes.MarkSandboxRunning;

internal sealed class MarkSandboxRunningCommandHandler(
    ISandboxRepository sandboxRepository,
    IUnitOfWork unitOfWork,
    IDateTimeProvider dateTimeProvider,
    ILogger<MarkSandboxRunningCommandHandler> logger
    ) : ICommandHandler<MarkSandboxRunningCommand>
{
    public async Task<Result> Handle(MarkSandboxRunningCommand request, CancellationToken cancellationToken)
    {
        var sandbox = await sandboxRepository.GetByIdAsync(request.SandboxId, cancellationToken);

        if (sandbox is null)
        {
            logger.LogWarning(
                "vm.ready for unknown sandbox {SandboxId} — ignoring",
                request.SandboxId);
            return Result.Failure(SandboxErrors.NotFound);
        }

        // Idempotent: at-least-once delivery may replay the same vm.ready event.
        // If the sandbox is already Running with the same endpoint, treat as success.
        if (sandbox.Status == SandboxStatus.Running &&
            sandbox.Host == request.Host &&
            sandbox.Port == request.Port)
        {
            return Result.Success();
        }

        var transition = sandbox.MarkRunning(request.Host, request.Port, dateTimeProvider.UtcNow);
        if (transition.IsFailure)
        {
            logger.LogWarning(
                "Could not transition sandbox {SandboxId} to Running: {Error}",
                request.SandboxId,
                transition.Error);
            return transition;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Sandbox {SandboxId} transitioned to Running at {Host}:{Port}",
            sandbox.Id,
            request.Host,
            request.Port);

        return Result.Success();
    }
}
