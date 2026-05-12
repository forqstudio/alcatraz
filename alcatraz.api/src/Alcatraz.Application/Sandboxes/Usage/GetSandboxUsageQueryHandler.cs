using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Clock;
using Alcatraz.Application.Abstractions.Data;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes.Usage;
using Dapper;

namespace Alcatraz.Application.Sandboxes.Usage;

internal sealed class GetSandboxUsageQueryHandler(
    ISqlConnectionFactory sqlConnectionFactory,
    IUserContext userContext,
    IDateTimeProvider dateTimeProvider
    ) : IQueryHandler<GetSandboxUsageQuery, SandboxUsageResponse>
{
    public async Task<Result<SandboxUsageResponse>> Handle(
        GetSandboxUsageQuery request,
        CancellationToken cancellationToken)
    {
        using var connection = sqlConnectionFactory.CreateConnection();

        const string finalisedSql = """
            SELECT
                r.id AS SandboxId,
                s.owner_user_id AS OwnerUserId,
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
            WHERE r.id = @SandboxId
            """;

        var finalised = await connection.QueryFirstOrDefaultAsync<FinalisedRow>(
            finalisedSql,
            new { request.SandboxId });

        if (finalised is not null)
        {
            if (finalised.OwnerUserId != userContext.UserId)
            {
                return Result.Failure<SandboxUsageResponse>(SandboxUsageErrors.SandboxNotFound);
            }

            return new SandboxUsageResponse
            {
                SandboxId = finalised.SandboxId,
                OwnerUserId = finalised.OwnerUserId,
                Finalised = true,
                BillingWindowStartUtc = finalised.BillingWindowStartUtc,
                BillingWindowEndUtc = finalised.BillingWindowEndUtc,
                ProvisionedVcpuSeconds = finalised.ProvisionedVcpuSeconds,
                ProvisionedMemoryMibSeconds = finalised.ProvisionedMemoryMibSeconds,
                ActualCpuUsageUsec = finalised.ActualCpuUsageUsec,
                ActualNetRxBytes = finalised.ActualNetRxBytes,
                ActualNetTxBytes = finalised.ActualNetTxBytes,
                SampleCount = finalised.SampleCount,
                FinalisedAtUtc = finalised.FinalisedAtUtc,
            };
        }

        // No finalised record yet — compute the live view from the sandbox and
        // its latest sample. A still-provisioning sandbox yields a zero-length
        // window with zero provisioned totals.
        const string sandboxSql = """
            SELECT
                id AS SandboxId,
                owner_user_id AS OwnerUserId,
                actual_vcpus AS ActualVcpus,
                actual_memory_mib AS ActualMemoryMib,
                created_on_utc AS CreatedOnUtc,
                ready_at_utc AS ReadyAtUtc,
                deleted_on_utc AS DeletedOnUtc
            FROM sandboxes
            WHERE id = @SandboxId
            """;

        var sandbox = await connection.QueryFirstOrDefaultAsync<SandboxRow>(
            sandboxSql,
            new { request.SandboxId });

        if (sandbox is null || sandbox.OwnerUserId != userContext.UserId)
        {
            return Result.Failure<SandboxUsageResponse>(SandboxUsageErrors.SandboxNotFound);
        }

        const string latestSampleSql = """
            SELECT
                cpu_usage_usec_cumulative AS Cpu,
                net_rx_bytes_cumulative AS Rx,
                net_tx_bytes_cumulative AS Tx
            FROM sandbox_usage_samples
            WHERE sandbox_id = @SandboxId
            ORDER BY sampled_at_utc DESC
            LIMIT 1
            """;

        var latest = await connection.QueryFirstOrDefaultAsync<SampleRow>(
            latestSampleSql,
            new { request.SandboxId });

        const string sampleCountSql = """
            SELECT COUNT(*) FROM sandbox_usage_samples WHERE sandbox_id = @SandboxId
            """;

        var sampleCount = await connection.ExecuteScalarAsync<int>(
            sampleCountSql,
            new { request.SandboxId });

        var now = dateTimeProvider.UtcNow;
        DateTime windowStart;
        DateTime windowEnd;
        long provisionedVcpuSeconds = 0;
        long provisionedMemoryMibSeconds = 0;

        if (sandbox.ReadyAtUtc is { } readyAt &&
            sandbox.ActualVcpus is { } vcpus &&
            sandbox.ActualMemoryMib is { } memMib)
        {
            windowStart = readyAt;
            windowEnd = sandbox.DeletedOnUtc ?? now;
            if (windowEnd < windowStart)
            {
                windowEnd = windowStart;
            }
            var windowSeconds = (long)Math.Max(0, (windowEnd - windowStart).TotalSeconds);
            provisionedVcpuSeconds = vcpus * windowSeconds;
            provisionedMemoryMibSeconds = memMib * windowSeconds;
        }
        else
        {
            // Still provisioning: no billing window has opened yet.
            windowStart = sandbox.CreatedOnUtc;
            windowEnd = sandbox.CreatedOnUtc;
        }

        return new SandboxUsageResponse
        {
            SandboxId = sandbox.SandboxId,
            OwnerUserId = sandbox.OwnerUserId,
            Finalised = false,
            BillingWindowStartUtc = windowStart,
            BillingWindowEndUtc = windowEnd,
            ProvisionedVcpuSeconds = provisionedVcpuSeconds,
            ProvisionedMemoryMibSeconds = provisionedMemoryMibSeconds,
            ActualCpuUsageUsec = latest?.Cpu,
            ActualNetRxBytes = latest?.Rx,
            ActualNetTxBytes = latest?.Tx,
            SampleCount = sampleCount,
            FinalisedAtUtc = null,
        };
    }

    private sealed class FinalisedRow
    {
        public Guid SandboxId { get; init; }
        public Guid OwnerUserId { get; init; }
        public DateTime BillingWindowStartUtc { get; init; }
        public DateTime BillingWindowEndUtc { get; init; }
        public long ProvisionedVcpuSeconds { get; init; }
        public long ProvisionedMemoryMibSeconds { get; init; }
        public long? ActualCpuUsageUsec { get; init; }
        public long? ActualNetRxBytes { get; init; }
        public long? ActualNetTxBytes { get; init; }
        public int SampleCount { get; init; }
        public DateTime FinalisedAtUtc { get; init; }
    }

    private sealed class SandboxRow
    {
        public Guid SandboxId { get; init; }
        public Guid OwnerUserId { get; init; }
        public int? ActualVcpus { get; init; }
        public int? ActualMemoryMib { get; init; }
        public DateTime CreatedOnUtc { get; init; }
        public DateTime? ReadyAtUtc { get; init; }
        public DateTime? DeletedOnUtc { get; init; }
    }

    private sealed class SampleRow
    {
        public long? Cpu { get; init; }
        public long? Rx { get; init; }
        public long? Tx { get; init; }
    }
}
