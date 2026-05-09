using Alcatraz.Application.Permissions.GetPermission;

namespace Alcatraz.Application.Roles.GetRole;

public sealed record RoleResponse(
    int Id,
    string Name,
    List<PermissionResponse> Permissions);
