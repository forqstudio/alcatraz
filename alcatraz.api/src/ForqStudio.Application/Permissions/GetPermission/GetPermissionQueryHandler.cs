using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;

namespace ForqStudio.Application.Permissions.GetPermission;

internal sealed class GetPermissionQueryHandler(IPermissionRepository permissionRepository)
    : IQueryHandler<GetPermissionQuery, PermissionResponse>
{
    public async Task<Result<PermissionResponse>> Handle(
        GetPermissionQuery request,
        CancellationToken cancellationToken)
    {
        var permission = await permissionRepository.GetByIdAsync(request.Id, cancellationToken);

        if (permission is null)
        {
            return Result.Failure<PermissionResponse>(PermissionErrors.NotFound);
        }

        return new PermissionResponse(permission.Id, permission.Name);
    }
}
