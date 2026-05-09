using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Application.Permissions.GetPermission;

namespace Alcatraz.Application.Permissions.GetPermissions;

public sealed record GetPermissionsQuery : IQuery<IReadOnlyList<PermissionResponse>>;
