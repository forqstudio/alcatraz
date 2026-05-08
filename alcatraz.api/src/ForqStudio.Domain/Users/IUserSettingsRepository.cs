namespace ForqStudio.Domain.Users;

public interface IUserSettingsRepository
{
    Task<UserSettings?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

    void Add(UserSettings userSettings);
}
