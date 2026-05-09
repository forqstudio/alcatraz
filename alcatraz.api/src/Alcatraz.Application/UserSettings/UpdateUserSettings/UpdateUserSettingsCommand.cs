using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.UserSettings.UpdateUserSettings;

public sealed record UpdateUserSettingsCommand(
    string? PreferredLanguage,
    bool? EmailNotificationsEnabled,
    string? Timezone) : ICommand;
