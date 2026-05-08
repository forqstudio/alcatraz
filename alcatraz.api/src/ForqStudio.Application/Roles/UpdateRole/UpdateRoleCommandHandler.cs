using ForqStudio.Application.Abstractions.Caching;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;

namespace ForqStudio.Application.Roles.UpdateRole;

internal sealed class UpdateRoleCommandHandler(
    IRoleRepository roleRepository,
    IPermissionRepository permissionRepository,
    ICacheService cacheService,
    IUnitOfWork unitOfWork)
    : ICommandHandler<UpdateRoleCommand>
{
    public async Task<Result> Handle(
        UpdateRoleCommand request,
        CancellationToken cancellationToken)
    {
        var role = await roleRepository.GetByIdAsync(request.Id, cancellationToken);

        if (role is null)
        {
            return Result.Failure(RoleErrors.NotFound);
        }

        if (role.Id <= Role.MaxSystemId)
        {
            return Result.Failure(RoleErrors.SystemRole);
        }

        var existingRole = await roleRepository.GetByNameAsync(request.Name, cancellationToken);
        if (existingRole is not null && existingRole.Id != request.Id)
        {
            return Result.Failure(RoleErrors.AlreadyExists);
        }

        var permissions = await permissionRepository.GetByIdsAsync(request.PermissionIds, cancellationToken);

        var foundIds = permissions.Select(p => p.Id).ToHashSet();
        var missingIds = request.PermissionIds.Where(id => !foundIds.Contains(id)).ToList();

        if (missingIds.Count > 0)
        {
            return Result.Failure(RoleErrors.PermissionsNotFound(missingIds));
        }

        role.UpdateName(request.Name);
        role.AssignPermissions(permissions.ToList());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        await InvalidateCachesAsync(request.Id, cancellationToken);

        return Result.Success();
    }

    private async Task InvalidateCachesAsync(int roleId, CancellationToken cancellationToken)
    {
        var identityIds = await roleRepository.GetUserIdentityIdsForRoleAsync(roleId, cancellationToken);

        var permissionKeys = identityIds.Select(CacheKeys.AuthPermissions);
        var roleKeys = identityIds.Select(CacheKeys.AuthRoles);
        await cacheService.RemoveManyAsync(permissionKeys.Concat(roleKeys), cancellationToken);
    }
}
