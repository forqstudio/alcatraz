using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Roles.AssignPermissions;

public sealed record AssignPermissionsCommand(
    int RoleId,
    List<int> PermissionIds) : ICommand;
