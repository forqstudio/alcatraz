using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Application.Permissions.GetPermission;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;

namespace ForqStudio.Application.Roles.GetRole;

internal sealed class GetRoleQueryHandler(IRoleRepository roleRepository)
    : IQueryHandler<GetRoleQuery, RoleResponse>
{
    public async Task<Result<RoleResponse>> Handle(
        GetRoleQuery request,
        CancellationToken cancellationToken)
    {
        var role = await roleRepository.GetByIdAsync(request.Id, cancellationToken);

        if (role is null)
        {
            return Result.Failure<RoleResponse>(RoleErrors.NotFound);
        }

        var permissions = role.Permissions
            .Select(p => new PermissionResponse(p.Id, p.Name))
            .ToList();

        return new RoleResponse(role.Id, role.Name, permissions);
    }
}
