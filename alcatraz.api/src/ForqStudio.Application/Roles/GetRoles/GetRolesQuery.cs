using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Application.Roles.GetRole;

namespace ForqStudio.Application.Roles.GetRoles;

public sealed record GetRolesQuery : IQuery<IReadOnlyList<RoleResponse>>;
