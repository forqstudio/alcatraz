using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Roles.DeleteRole;

public sealed record DeleteRoleCommand(int Id) : ICommand;
