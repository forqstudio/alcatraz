using ForqStudio.Application.Abstractions.Caching;
using ForqStudio.Application.Abstractions.Data;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;
using Dapper;

namespace ForqStudio.Application.Permissions.UpdatePermission;

internal sealed class UpdatePermissionCommandHandler(
    IPermissionRepository permissionRepository,
    ICacheService cacheService,
    ISqlConnectionFactory sqlConnectionFactory,
    IUnitOfWork unitOfWork)
    : ICommandHandler<UpdatePermissionCommand>
{
    public async Task<Result> Handle(
        UpdatePermissionCommand request,
        CancellationToken cancellationToken)
    {
        var permission = await permissionRepository.GetByIdAsync(request.Id, cancellationToken);

        if (permission is null)
        {
            return Result.Failure(PermissionErrors.NotFound);
        }

        if (permission.Id <= Permission.MaxSystemId)
        {
            return Result.Failure(PermissionErrors.SystemPermission);
        }

        if (!Permission.IsValidName(request.Name))
        {
            return Result.Failure(PermissionErrors.InvalidName);
        }

        var existingPermission = await permissionRepository.GetByNameAsync(request.Name, cancellationToken);
        if (existingPermission is not null && existingPermission.Id != request.Id)
        {
            return Result.Failure(PermissionErrors.AlreadyExists);
        }

        permission.UpdateName(request.Name);

        permissionRepository.Update(permission);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        await InvalidateCachesAsync(request.Id, cancellationToken);

        return Result.Success();
    }

    private async Task InvalidateCachesAsync(int permissionId, CancellationToken cancellationToken)
    {
        using var connection = sqlConnectionFactory.CreateConnection();

        const string sql = """
            SELECT DISTINCT u.identity_id
            FROM users u
            INNER JOIN role_user ru ON u.id = ru.users_id
            INNER JOIN role_permissions rp ON ru.roles_id = rp.role_id
            WHERE rp.permission_id = @PermissionId
            """;

        var identityIds = await connection.QueryAsync<string>(sql, new { PermissionId = permissionId });

        var permissionKeys = identityIds.Select(CacheKeys.AuthPermissions);
        var roleKeys = identityIds.Select(CacheKeys.AuthRoles);
        await cacheService.RemoveManyAsync(permissionKeys.Concat(roleKeys), cancellationToken);
    }
}
