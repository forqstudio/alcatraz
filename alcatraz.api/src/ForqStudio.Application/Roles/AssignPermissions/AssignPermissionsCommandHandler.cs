using ForqStudio.Application.Abstractions.Caching;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;

namespace ForqStudio.Application.Roles.AssignPermissions;

internal sealed class AssignPermissionsCommandHandler(
    IRoleRepository roleRepository,
    IPermissionRepository permissionRepository,
    ICacheService cacheService,
    IUnitOfWork unitOfWork)
    : ICommandHandler<AssignPermissionsCommand>
{
    public async Task<Result> Handle(
        AssignPermissionsCommand request,
        CancellationToken cancellationToken)
    {
        var role = await roleRepository.GetByIdAsync(request.RoleId, cancellationToken);

        if (role is null)
        {
            return Result.Failure(RoleErrors.NotFound);
        }

        var permissions = await permissionRepository.GetByIdsAsync(request.PermissionIds, cancellationToken);

        var foundIds = permissions.Select(p => p.Id).ToHashSet();
        var missingIds = request.PermissionIds.Where(id => !foundIds.Contains(id)).ToList();

        if (missingIds.Count > 0)
        {
            return Result.Failure(RoleErrors.PermissionsNotFound(missingIds));
        }

        role.AddPermissions(permissions.ToList());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        await InvalidateCachesAsync(request.RoleId, cancellationToken);

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
