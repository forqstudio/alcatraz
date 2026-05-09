using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Users.LogInUser;

public sealed record LogInUserCommand(string Email, string Password)
    : ICommand<AccessTokenResponse>;