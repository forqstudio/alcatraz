using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Data;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;
using Dapper;

namespace Alcatraz.Application.Sandboxes.Usage;

internal sealed class ListSandboxUsageQueryHandler(
    ISqlConnectionFactory sqlConnectionFactory,
    IUserContext userContext
    ) : IQueryHandler<ListSandboxUsageQuery, IReadOnlyList<SandboxUsageResponse>>
{
    public async Task<Result<IReadOnlyList<SandboxUsageResponse>>> Handle(
        ListSandboxUsageQuery request,
        CancellationToken cancellationToken)
    {
        using var connection = sqlConnectionFactory.CreateConnection();

        const string sql = """
            SELECT
                r.id AS SandboxId,
                s.owner_user_id AS OwnerUserId,
                TRUE AS Finalised,
                r.billing_window_start_utc AS BillingWindowStartUtc,
                r.billing_window_end_utc AS BillingWindowEndUtc,
                r.provisioned_vcpu_seconds AS ProvisionedVcpuSeconds,
                r.provisioned_memory_mib_seconds AS ProvisionedMemoryMibSeconds,
                r.actual_cpu_usage_usec AS ActualCpuUsageUsec,
                r.actual_net_rx_bytes AS ActualNetRxBytes,
                r.actual_net_tx_bytes AS ActualNetTxBytes,
                r.sample_count AS SampleCount,
                r.finalised_at_utc AS FinalisedAtUtc
            FROM sandbox_usage_records r
            JOIN sandboxes s ON s.id = r.id
            WHERE s.owner_user_id = @OwnerUserId
            ORDER BY r.billing_window_end_utc DESC
            """;

        var rows = await connection.QueryAsync<SandboxUsageResponse>(
            sql,
            new { OwnerUserId = userContext.UserId });

        return Result.Success<IReadOnlyList<SandboxUsageResponse>>(rows.AsList());
    }
}
