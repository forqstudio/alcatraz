using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Application.Permissions.GetPermission;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;

namespace ForqStudio.Application.Permissions.GetPermissions;

internal sealed class GetPermissionsQueryHandler(IPermissionRepository permissionRepository)
    : IQueryHandler<GetPermissionsQuery, IReadOnlyList<PermissionResponse>>
{
    public async Task<Result<IReadOnlyList<PermissionResponse>>> Handle(
        GetPermissionsQuery request,
        CancellationToken cancellationToken)
    {
        var permissions = await permissionRepository.GetAllAsync(cancellationToken);

        var response = permissions
            .Select(p => new PermissionResponse(p.Id, p.Name))
            .ToList();

        return response;
    }
}
