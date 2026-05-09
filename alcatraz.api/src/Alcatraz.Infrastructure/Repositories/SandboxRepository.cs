using Alcatraz.Domain.Sandboxes;

namespace Alcatraz.Infrastructure.Repositories;

internal sealed class SandboxRepository(ApplicationDbContext dbContext)
    : Repository<Sandbox>(dbContext), ISandboxRepository
{
}
