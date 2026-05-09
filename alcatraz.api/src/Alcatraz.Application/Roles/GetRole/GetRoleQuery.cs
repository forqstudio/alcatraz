using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Roles.GetRole;

public sealed record GetRoleQuery(int Id) : IQuery<RoleResponse>;
