using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Application.Permissions.GetPermission;
using ForqStudio.Application.Roles.GetRole;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;

namespace ForqStudio.Application.Roles.GetRoles;

internal sealed class GetRolesQueryHandler(IRoleRepository roleRepository)
    : IQueryHandler<GetRolesQuery, IReadOnlyList<RoleResponse>>
{
    public async Task<Result<IReadOnlyList<RoleResponse>>> Handle(
        GetRolesQuery request,
        CancellationToken cancellationToken)
    {
        var roles = await roleRepository.GetAllAsync(cancellationToken);

        var response = roles
            .Select(r => new RoleResponse(
                r.Id,
                r.Name,
                r.Permissions.Select(p => new PermissionResponse(p.Id, p.Name)).ToList()))
            .ToList();

        return response;
    }
}
