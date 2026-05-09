using Microsoft.AspNetCore.Authorization;

namespace Alcatraz.Infrastructure.Authorization;

internal sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}
