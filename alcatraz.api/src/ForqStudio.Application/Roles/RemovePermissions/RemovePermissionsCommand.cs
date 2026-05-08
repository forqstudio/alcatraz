using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Roles.RemovePermissions;

public sealed record RemovePermissionsCommand(
    int RoleId,
    List<int> PermissionIds) : ICommand;
