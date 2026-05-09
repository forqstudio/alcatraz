using Alcatraz.Application.Abstractions.Caching;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Users;

namespace Alcatraz.Application.Roles.RemovePermissions;

internal sealed class RemovePermissionsCommandHandler(
    IRoleRepository roleRepository,
    ICacheService cacheService,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RemovePermissionsCommand>
{
    public async Task<Result> Handle(
        RemovePermissionsCommand request,
        CancellationToken cancellationToken)
    {
        var role = await roleRepository.GetByIdAsync(request.RoleId, cancellationToken);

        if (role is null)
        {
            return Result.Failure(RoleErrors.NotFound);
        }

        role.RemovePermissions(request.PermissionIds);

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
