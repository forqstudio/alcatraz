using ForqStudio.Domain.Users;

namespace ForqStudio.Application.UnitTests.Permissions;

internal static class PermissionData
{
    public static Permission Create(int id = 100, string name = "test.permission")
    {
        return Permission.Create(id, name);
    }

    public static Permission CreateSystemPermission(int id = 1, string name = "users.read")
    {
        return Permission.Create(id, name);
    }
}
