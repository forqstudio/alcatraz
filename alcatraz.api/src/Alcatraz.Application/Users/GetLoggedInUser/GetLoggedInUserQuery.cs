using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Users.GetLoggedInUser;

public sealed record GetLoggedInUserQuery : IQuery<UserResponse>;