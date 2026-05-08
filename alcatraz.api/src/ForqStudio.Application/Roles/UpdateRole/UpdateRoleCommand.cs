using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Roles.UpdateRole;

public sealed record UpdateRoleCommand(
    int Id,
    string Name,
    List<int> PermissionIds) : ICommand;
