using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Application.Permissions.GetPermission;
using Alcatraz.Application.Roles.GetRole;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Users;

namespace Alcatraz.Application.Roles.GetRoles;

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
