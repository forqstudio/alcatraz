namespace ForqStudio.Domain.Users;

public sealed class RolePermission
{
    public int RoleId { get; init; }

    public int PermissionId { get; init; }


    public RolePermission(int roleId, int permissionId)
    {
        RoleId = roleId;
        PermissionId = permissionId;
    }   
}
