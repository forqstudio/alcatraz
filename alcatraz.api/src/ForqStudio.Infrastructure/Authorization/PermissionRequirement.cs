using Microsoft.AspNetCore.Authorization;

namespace ForqStudio.Infrastructure.Authorization;

internal sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}
