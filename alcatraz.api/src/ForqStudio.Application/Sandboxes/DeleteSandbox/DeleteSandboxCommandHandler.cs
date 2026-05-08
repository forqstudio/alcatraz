using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Clock;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Sandboxes;

namespace ForqStudio.Application.Sandboxes.DeleteSandbox;

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
