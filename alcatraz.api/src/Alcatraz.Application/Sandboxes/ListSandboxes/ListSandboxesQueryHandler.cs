using Dapper;
using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Data;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes;

namespace Alcatraz.Application.Sandboxes.ListSandboxes;

internal sealed class ListSandboxesQueryHandler(
    ISqlConnectionFactory sqlConnectionFactory,
    IUserContext userContext
    ) : IQueryHandler<ListSandboxesQuery, IReadOnlyList<SandboxResponse>>
{
    public async Task<Result<IReadOnlyList<SandboxResponse>>> Handle(
        ListSandboxesQuery request,
        CancellationToken cancellationToken)
    {
        using var connection = sqlConnectionFactory.CreateConnection();

        const string sql = """
            SELECT
                id AS Id,
                owner_user_id AS OwnerUserId,
                requested_vcpus AS Vcpus,
                requested_memory_mib AS MemoryMib,
                status AS Status,
                created_on_utc AS CreatedOnUtc,
                deleted_on_utc AS DeletedOnUtc
            FROM sandboxes
            WHERE owner_user_id = @OwnerUserId
              AND status <> @DeletedStatus
            ORDER BY created_on_utc DESC
            """;

        var sandboxes = await connection.QueryAsync<SandboxResponse>(
            sql,
            new
            {
                OwnerUserId = userContext.UserId,
                DeletedStatus = (int)SandboxStatus.Deleted,
            });

        return Result.Success<IReadOnlyList<SandboxResponse>>(sandboxes.AsList());
    }
}
