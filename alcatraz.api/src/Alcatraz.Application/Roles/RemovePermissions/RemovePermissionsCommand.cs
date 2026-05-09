using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Roles.RemovePermissions;

public sealed record RemovePermissionsCommand(
    int RoleId,
    List<int> PermissionIds) : ICommand;
