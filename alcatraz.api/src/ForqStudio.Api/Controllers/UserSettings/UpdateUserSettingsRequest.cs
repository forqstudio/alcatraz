namespace ForqStudio.Api.Controllers.UserSettings;

public sealed record UpdateUserSettingsRequest(
    string? PreferredLanguage,
    bool? EmailNotificationsEnabled,
    string? Timezone);
