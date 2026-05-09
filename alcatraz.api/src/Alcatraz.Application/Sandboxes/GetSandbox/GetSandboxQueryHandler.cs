using Dapper;
using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Data;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes;

namespace Alcatraz.Application.Sandboxes.GetSandbox;

internal sealed class GetSandboxQueryHandler(
    ISqlConnectionFactory sqlConnectionFactory,
    IUserContext userContext
    ) : IQueryHandler<GetSandboxQuery, SandboxResponse>
{
    public async Task<Result<SandboxResponse>> Handle(GetSandboxQuery request, CancellationToken cancellationToken)
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
            WHERE id = @SandboxId
            """;

        var sandbox = await connection.QueryFirstOrDefaultAsync<SandboxResponse>(
            sql,
            new { request.SandboxId });

        if (sandbox is null || sandbox.OwnerUserId != userContext.UserId)
        {
            return Result.Failure<SandboxResponse>(SandboxErrors.NotFound);
        }

        return sandbox;
    }
}
