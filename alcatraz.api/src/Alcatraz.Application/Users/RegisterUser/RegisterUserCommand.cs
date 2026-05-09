using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Users.RegisterUser;

public sealed record RegisterUserCommand(
        string Email,
        string FirstName,
        string LastName,
        string Password) : ICommand<Guid>;