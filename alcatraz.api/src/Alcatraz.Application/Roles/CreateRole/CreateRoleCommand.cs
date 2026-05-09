using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Roles.CreateRole;

public sealed record CreateRoleCommand(
    string Name,
    List<int> PermissionIds) : ICommand<int>;
