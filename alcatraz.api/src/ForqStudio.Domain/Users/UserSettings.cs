using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Users;

public sealed class UserSettings : Entity
{
    private UserSettings(Guid id, Guid userId) : base(id)
    {
        UserId = userId;
    }

    private UserSettings()
    {
    }

    public Guid UserId { get; private set; }

    public string? PreferredLanguage { get; private set; }

    public bool? EmailNotificationsEnabled { get; private set; }

    public string? Timezone { get; private set; }

    public static UserSettings Create(Guid userId)
    {
        return new UserSettings(Guid.NewGuid(), userId);
    }

    public Result Update(
        string? preferredLanguage,
        bool? emailNotificationsEnabled,
        string? timezone)
    {
        if (preferredLanguage is not null) PreferredLanguage = preferredLanguage;
        if (emailNotificationsEnabled is not null) EmailNotificationsEnabled = emailNotificationsEnabled;
        if (timezone is not null) Timezone = timezone;

        return Result.Success();
    }
}
