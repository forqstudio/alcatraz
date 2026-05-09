using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Clock;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes;

namespace Alcatraz.Application.Sandboxes.DeleteSandbox;

internal sealed class DeleteSandboxCommandHandler(
    ISandboxRepository sandboxRepository,
    IUserContext userContext,
    IUnitOfWork unitOfWork,
    IDateTimeProvider dateTimeProvider
    ) : ICommandHandler<DeleteSandboxCommand>
{
    public async Task<Result> Handle(DeleteSandboxCommand request, CancellationToken cancellationToken)
    {
        var sandbox = await sandboxRepository.GetByIdAsync(request.SandboxId, cancellationToken);

        if (sandbox is null)
        {
            return Result.Failure(SandboxErrors.NotFound);
        }

        var ownership = sandbox.EnsureOwnedBy(userContext.UserId);
        if (ownership.IsFailure)
        {
            return ownership;
        }

        var deletion = sandbox.MarkDeleting(dateTimeProvider.UtcNow);
        if (deletion.IsFailure)
        {
            return deletion;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
