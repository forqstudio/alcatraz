using ForqStudio.Application.Permissions.GetPermission;

namespace ForqStudio.Application.Roles.GetRole;

public sealed record RoleResponse(
    int Id,
    string Name,
    List<PermissionResponse> Permissions);
