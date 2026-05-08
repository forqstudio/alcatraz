using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Roles.GetRole;

public sealed record GetRoleQuery(int Id) : IQuery<RoleResponse>;
