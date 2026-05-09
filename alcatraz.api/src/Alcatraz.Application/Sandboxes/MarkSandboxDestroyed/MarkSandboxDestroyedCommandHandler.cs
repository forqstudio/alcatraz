using Alcatraz.Application.Abstractions.Clock;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes;
using Microsoft.Extensions.Logging;

namespace Alcatraz.Application.Sandboxes.MarkSandboxDestroyed;

internal sealed class MarkSandboxDestroyedCommandHandler(
    ISandboxRepository sandboxRepository,
    IUnitOfWork unitOfWork,
    IDateTimeProvider dateTimeProvider,
    ILogger<MarkSandboxDestroyedCommandHandler> logger
    ) : ICommandHandler<MarkSandboxDestroyedCommand>
{
    public async Task<Result> Handle(MarkSandboxDestroyedCommand request, CancellationToken cancellationToken)
    {
        var sandbox = await sandboxRepository.GetByIdAsync(request.SandboxId, cancellationToken);

        if (sandbox is null)
        {
            logger.LogWarning(
                "vm.destroyed for unknown sandbox {SandboxId} — ignoring",
                request.SandboxId);
            return Result.Failure(SandboxErrors.NotFound);
        }

        var previous = sandbox.Status;

        var transition = sandbox.MarkDestroyed(dateTimeProvider.UtcNow);
        if (transition.IsFailure)
        {
            logger.LogWarning(
                "Could not transition sandbox {SandboxId} from {From} on vm.destroyed: {Error}",
                request.SandboxId,
                previous,
                transition.Error);
            return transition;
        }

        if (previous == sandbox.Status)
        {
            return Result.Success();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Sandbox {SandboxId} transitioned {From} → {To} on vm.destroyed",
            sandbox.Id,
            previous,
            sandbox.Status);

        return Result.Success();
    }
}
