using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Roles.AssignPermissions;

public sealed record AssignPermissionsCommand(
    int RoleId,
    List<int> PermissionIds) : ICommand;
