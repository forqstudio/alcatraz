using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Users;

public static class RoleErrors
{
    public static Error NotFound = new(
        "Role.NotFound",
        "The role with the specified identifier was not found");

    public static Error AlreadyExists = new(
        "Role.AlreadyExists",
        "A role with this name already exists");

    public static Error InUse = new(
        "Role.InUse",
        "Cannot delete role because it is assigned to one or more users");

    public static Error SystemRole = new(
        "Role.SystemRole",
        "Cannot delete system roles");

    public static Error PermissionNotFound = new(
        "Role.PermissionNotFound",
        "One or more specified permissions were not found");

    public static Error PermissionsNotFound(IEnumerable<int> missingIds) => new(
        "Role.PermissionsNotFound",
        $"The following permission IDs were not found: {string.Join(", ", missingIds)}");
}
