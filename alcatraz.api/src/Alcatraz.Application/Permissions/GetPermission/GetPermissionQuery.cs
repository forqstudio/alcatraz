using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Permissions.GetPermission;

public sealed record GetPermissionQuery(int Id) : IQuery<PermissionResponse>;
