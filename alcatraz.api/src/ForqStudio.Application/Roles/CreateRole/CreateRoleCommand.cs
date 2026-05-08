using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Roles.CreateRole;

public sealed record CreateRoleCommand(
    string Name,
    List<int> PermissionIds) : ICommand<int>;
