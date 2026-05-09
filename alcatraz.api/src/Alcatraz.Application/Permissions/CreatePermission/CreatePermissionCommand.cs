using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Permissions.CreatePermission;

public sealed record CreatePermissionCommand(string Name) : ICommand<int>;
