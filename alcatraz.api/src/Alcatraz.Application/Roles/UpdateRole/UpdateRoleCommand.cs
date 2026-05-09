using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Roles.UpdateRole;

public sealed record UpdateRoleCommand(
    int Id,
    string Name,
    List<int> PermissionIds) : ICommand;
