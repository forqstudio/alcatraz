using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Users;

public static class UserSettingsErrors
{
    public static Error NotFound = new(
        "UserSettings.NotFound",
        "The user settings for the specified user were not found");
}
