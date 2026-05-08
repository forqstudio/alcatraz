namespace ForqStudio.Api.Controllers.Roles;

public sealed record UpdateRoleRequest(string Name, List<int> PermissionIds);
