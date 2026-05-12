using Alcatraz.Domain.Sandboxes.Usage;
using Microsoft.EntityFrameworkCore;

namespace Alcatraz.Infrastructure.Repositories;

internal sealed class SandboxUsageRecordRepository(ApplicationDbContext dbContext)
    : Repository<SandboxUsageRecord>(dbContext), ISandboxUsageRecordRepository
{
    public Task<SandboxUsageRecord?> GetBySandboxIdAsync(
        Guid sandboxId,
        CancellationToken cancellationToken = default) =>
        DbContext.Set<SandboxUsageRecord>()
            .FirstOrDefaultAsync(record => record.Id == sandboxId, cancellationToken);
}
