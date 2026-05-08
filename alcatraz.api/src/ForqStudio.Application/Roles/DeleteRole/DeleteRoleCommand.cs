using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Roles.DeleteRole;

public sealed record DeleteRoleCommand(int Id) : ICommand;
