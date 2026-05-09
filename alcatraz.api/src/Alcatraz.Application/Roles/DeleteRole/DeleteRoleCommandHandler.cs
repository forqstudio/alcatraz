using Alcatraz.Application.Abstractions.Caching;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Users;

namespace Alcatraz.Application.Roles.DeleteRole;

internal sealed class DeleteRoleCommandHandler(
    IRoleRepository roleRepository,
    ICacheService cacheService,
    IUnitOfWork unitOfWork)
    : ICommandHandler<DeleteRoleCommand>
{
    public async Task<Result> Handle(
        DeleteRoleCommand request,
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

        var isInUse = await roleRepository.IsInUseAsync(request.Id, cancellationToken);
        if (isInUse)
        {
            return Result.Failure(RoleErrors.InUse);
        }

        var identityIds = await roleRepository.GetUserIdentityIdsForRoleAsync(request.Id, cancellationToken);

        roleRepository.Delete(role);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var permissionKeys = identityIds.Select(CacheKeys.AuthPermissions);
        var roleKeys = identityIds.Select(CacheKeys.AuthRoles);
        await cacheService.RemoveManyAsync(permissionKeys.Concat(roleKeys), cancellationToken);

        return Result.Success();
    }
}
