using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Permissions.GetPermission;

public sealed record GetPermissionQuery(int Id) : IQuery<PermissionResponse>;
