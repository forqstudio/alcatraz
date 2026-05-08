namespace ForqStudio.Domain.Users;

public sealed class Role
{
    public const int MaxSystemId = 10;
    public const int NameMaxLength = 50;

    public static readonly Role User = new(1, Roles.User);

    public int Id { get; init; }

    public string Name { get; private set; } = string.Empty;

    public ICollection<User> Users { get; init; } = new List<User>();

    public ICollection<Permission> Permissions { get; private set; } = new List<Permission>();

    public Role(int id, string name)
    {
        Id = id;
        Name = name;
    }

    public static Role Create(int id, string name)
    {
        return new Role(id, name);
    }

    public void UpdateName(string name)
    {
        Name = name;
    }

    public void AssignPermissions(IList<Permission> permissions)
    {
        Permissions = new List<Permission>(permissions);
    }

    public void AddPermissions(IList<Permission> permissions)
    {
        foreach (var permission in permissions)
        {
            if (!Permissions.Any(p => p.Id == permission.Id))
            {
                Permissions.Add(permission);
            }
        }
    }

    public void RemovePermissions(IList<int> permissionIds)
    {
        var permissionsToRemove = Permissions.Where(p => permissionIds.Contains(p.Id)).ToList();
        foreach (var permission in permissionsToRemove)
        {
            Permissions.Remove(permission);
        }
    }
}
