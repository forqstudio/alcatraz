using ForqStudio.Domain.Users;

namespace ForqStudio.Infrastructure.Repositories;

internal sealed class UserRepository(ApplicationDbContext dbContext) : Repository<User>(dbContext), IUserRepository
{
    public override void Add(User user)
    {
        foreach (var role in user.Roles)
        {
            DbContext.Attach(role);
        }

        DbContext.Add(user);
    }
}