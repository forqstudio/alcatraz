using ForqStudio.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace ForqStudio.Infrastructure.Repositories;

internal sealed class UserSettingsRepository(ApplicationDbContext dbContext)
    : Repository<UserSettings>(dbContext), IUserSettingsRepository
{
    public async Task<UserSettings?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await DbContext
            .Set<UserSettings>()
            .FirstOrDefaultAsync(us => us.UserId == userId, cancellationToken);
    }
}
