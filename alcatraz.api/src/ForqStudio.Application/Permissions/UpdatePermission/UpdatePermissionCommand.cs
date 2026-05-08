using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Permissions.UpdatePermission;

public sealed record UpdatePermissionCommand(int Id, string Name) : ICommand;
