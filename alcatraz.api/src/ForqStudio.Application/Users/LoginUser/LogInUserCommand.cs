using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Users.LogInUser;

public sealed record LogInUserCommand(string Email, string Password)
    : ICommand<AccessTokenResponse>;