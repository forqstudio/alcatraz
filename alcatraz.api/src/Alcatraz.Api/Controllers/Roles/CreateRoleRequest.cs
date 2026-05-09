namespace Alcatraz.Api.Controllers.Roles;

public sealed record CreateRoleRequest(string Name, List<int> PermissionIds);
