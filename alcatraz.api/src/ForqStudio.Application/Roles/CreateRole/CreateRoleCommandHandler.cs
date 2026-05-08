using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;

namespace ForqStudio.Application.Roles.CreateRole;

internal sealed class CreateRoleCommandHandler(
    IRoleRepository roleRepository,
    IPermissionRepository permissionRepository,
    IUnitOfWork unitOfWork)
    : ICommandHandler<CreateRoleCommand, int>
{
    public async Task<Result<int>> Handle(
        CreateRoleCommand request,
        CancellationToken cancellationToken)
    {
        var existingRole = await roleRepository.GetByNameAsync(request.Name, cancellationToken);
        if (existingRole is not null)
        {
            return Result.Failure<int>(RoleErrors.AlreadyExists);
        }

        var permissions = await permissionRepository.GetByIdsAsync(request.PermissionIds, cancellationToken);

        var foundIds = permissions.Select(p => p.Id).ToHashSet();
        var missingIds = request.PermissionIds.Where(id => !foundIds.Contains(id)).ToList();

        if (missingIds.Count > 0)
        {
            return Result.Failure<int>(RoleErrors.PermissionsNotFound(missingIds));
        }

        var role = Role.Create(0, request.Name);

        role.AssignPermissions(permissions.ToList());

        roleRepository.Add(role);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return role.Id;
    }
}
