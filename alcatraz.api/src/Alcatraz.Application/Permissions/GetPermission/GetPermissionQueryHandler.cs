using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Users;

namespace Alcatraz.Application.Permissions.GetPermission;

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
