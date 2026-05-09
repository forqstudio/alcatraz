using Alcatraz.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Alcatraz.Infrastructure.Repositories;

internal sealed class UserRepository(ApplicationDbContext dbContext) : Repository<User>(dbContext), IUserRepository
{
    public Task<User?> GetByIdentityIdAsync(string identityId, CancellationToken cancellationToken = default) =>
        DbContext.Set<User>().FirstOrDefaultAsync(u => u.IdentityId == identityId, cancellationToken);

    public override void Add(User user)
    {
        foreach (var role in user.Roles)
        {
            DbContext.Attach(role);
        }

        DbContext.Add(user);
    }
}