using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.UserSettings.GetUserSettings;

public sealed record GetUserSettingsQuery : IQuery<UserSettingsResponse>;
