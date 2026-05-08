using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Users.GetLoggedInUser;

public sealed record GetLoggedInUserQuery : IQuery<UserResponse>;