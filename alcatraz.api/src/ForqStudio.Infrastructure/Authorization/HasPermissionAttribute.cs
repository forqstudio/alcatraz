using Microsoft.AspNetCore.Authorization;

namespace ForqStudio.Infrastructure.Authorization;

public sealed class HasPermissionAttribute(string permission) : AuthorizeAttribute(permission)
{
}
