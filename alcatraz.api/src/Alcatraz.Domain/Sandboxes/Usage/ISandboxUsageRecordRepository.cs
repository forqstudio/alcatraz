namespace Alcatraz.Domain.Sandboxes.Usage;

public interface ISandboxUsageRecordRepository
{
    Task<SandboxUsageRecord?> GetBySandboxIdAsync(
        Guid sandboxId,
        CancellationToken cancellationToken = default);

    void Add(SandboxUsageRecord record);
}
