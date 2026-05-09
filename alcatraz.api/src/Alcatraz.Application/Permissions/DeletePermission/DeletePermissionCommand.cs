using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Permissions.DeletePermission;

public sealed record DeletePermissionCommand(int Id) : ICommand;
