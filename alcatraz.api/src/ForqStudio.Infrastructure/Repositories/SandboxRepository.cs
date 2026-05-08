using ForqStudio.Domain.Sandboxes;

namespace ForqStudio.Infrastructure.Repositories;

internal sealed class SandboxRepository(ApplicationDbContext dbContext)
    : Repository<Sandbox>(dbContext), ISandboxRepository
{
}
