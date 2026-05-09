using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Application.Permissions.GetPermission;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Users;

namespace Alcatraz.Application.Permissions.GetPermissions;

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
