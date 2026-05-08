using ForqStudio.Domain.Users;

namespace ForqStudio.Infrastructure.Authorization;

public class UserRolesResponse
{
    public Guid UserId { get; init; }

    public List<Role> Roles { get; init; } = [];
}