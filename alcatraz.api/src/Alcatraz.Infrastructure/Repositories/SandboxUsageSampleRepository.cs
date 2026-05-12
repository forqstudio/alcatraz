using Alcatraz.Domain.Sandboxes.Usage;
using Microsoft.EntityFrameworkCore;

namespace Alcatraz.Infrastructure.Repositories;

internal sealed class SandboxUsageSampleRepository(ApplicationDbContext dbContext)
    : Repository<SandboxUsageSample>(dbContext), ISandboxUsageSampleRepository
{
    public Task<bool> ExistsAsync(
        Guid sandboxId,
        DateTime sampledAtUtc,
        CancellationToken cancellationToken = default) =>
        DbContext.Set<SandboxUsageSample>()
            .AsNoTracking()
            .AnyAsync(
                sample => sample.SandboxId == sandboxId && sample.SampledAtUtc == sampledAtUtc,
                cancellationToken);
}
