using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;

namespace ForqStudio.Application.Permissions.DeletePermission;

internal sealed class DeletePermissionCommandHandler(
    IPermissionRepository permissionRepository,
    IUnitOfWork unitOfWork)
    : ICommandHandler<DeletePermissionCommand>
{
    public async Task<Result> Handle(
        DeletePermissionCommand request,
        CancellationToken cancellationToken)
    {
        var permission = await permissionRepository.GetByIdAsync(request.Id, cancellationToken);

        if (permission is null)
        {
            return Result.Failure(PermissionErrors.NotFound);
        }

        if (permission.Id <= Permission.MaxSystemId)
        {
            return Result.Failure(PermissionErrors.SystemPermission);
        }

        var isInUse = await permissionRepository.IsInUseAsync(request.Id, cancellationToken);
        if (isInUse)
        {
            return Result.Failure(PermissionErrors.InUse);
        }

        permission.MarkAsDeleted();

        permissionRepository.Update(permission);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
