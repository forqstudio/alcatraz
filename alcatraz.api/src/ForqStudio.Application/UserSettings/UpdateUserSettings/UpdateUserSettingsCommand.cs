using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.UserSettings.UpdateUserSettings;

public sealed record UpdateUserSettingsCommand(
    string? PreferredLanguage,
    bool? EmailNotificationsEnabled,
    string? Timezone) : ICommand;
