using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Application.Permissions.GetPermission;

namespace ForqStudio.Application.Permissions.GetPermissions;

public sealed record GetPermissionsQuery : IQuery<IReadOnlyList<PermissionResponse>>;
