namespace Alcatraz.Api.Controllers.Roles;

public sealed record AssignPermissionsRequest(List<int> PermissionIds);
