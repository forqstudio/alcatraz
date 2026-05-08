using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Permissions.CreatePermission;

public sealed record CreatePermissionCommand(string Name) : ICommand<int>;
