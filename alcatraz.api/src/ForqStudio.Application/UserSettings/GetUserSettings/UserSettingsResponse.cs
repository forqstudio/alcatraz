namespace ForqStudio.Application.UserSettings.GetUserSettings;

public sealed record UserSettingsResponse(
    Guid Id,
    Guid UserId,
    string? PreferredLanguage,
    bool? EmailNotificationsEnabled,
    string? Timezone);
