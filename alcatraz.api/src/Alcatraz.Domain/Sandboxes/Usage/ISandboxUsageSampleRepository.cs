namespace Alcatraz.Domain.Sandboxes.Usage;

public interface ISandboxUsageSampleRepository
{
    void Add(SandboxUsageSample sample);

    Task<bool> ExistsAsync(
        Guid sandboxId,
        DateTime sampledAtUtc,
        CancellationToken cancellationToken = default);
}
