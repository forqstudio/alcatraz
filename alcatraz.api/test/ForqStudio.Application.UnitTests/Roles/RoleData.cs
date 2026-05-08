using ForqStudio.Domain.Users;

namespace ForqStudio.Application.UnitTests.Roles;

internal static class RoleData
{
    public static Role Create(int id = 100, string name = "testrole")
    {
        return Role.Create(id, name);
    }

    public static Role CreateSystemRole(int id = 1, string name = "registered")
    {
        return Role.Create(id, name);
    }

    public static Role CreateWithPermissions(int id = 100, string name = "testrole", params Permission[] permissions)
    {
        var role = Role.Create(id, name);
        role.AssignPermissions(permissions.ToList());
        return role;
    }
}
