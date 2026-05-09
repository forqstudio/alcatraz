using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.UserSettings.GetUserSettings;

public sealed record GetUserSettingsQuery : IQuery<UserSettingsResponse>;
