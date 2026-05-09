using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Application.Roles.GetRole;

namespace Alcatraz.Application.Roles.GetRoles;

public sealed record GetRolesQuery : IQuery<IReadOnlyList<RoleResponse>>;
