using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Permissions.UpdatePermission;

public sealed record UpdatePermissionCommand(int Id, string Name) : ICommand;
