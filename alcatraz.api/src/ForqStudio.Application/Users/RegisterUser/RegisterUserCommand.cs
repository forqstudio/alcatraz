using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Users.RegisterUser;

public sealed record RegisterUserCommand(
        string Email,
        string FirstName,
        string LastName,
        string Password) : ICommand<Guid>;