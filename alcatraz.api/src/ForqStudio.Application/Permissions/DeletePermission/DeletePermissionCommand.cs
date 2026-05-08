using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Permissions.DeletePermission;

public sealed record DeletePermissionCommand(int Id) : ICommand;
