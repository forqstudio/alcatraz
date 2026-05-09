using Microsoft.AspNetCore.Authorization;

namespace Alcatraz.Infrastructure.Authorization;

public sealed class HasPermissionAttribute(string permission) : AuthorizeAttribute(permission)
{
}
