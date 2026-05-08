using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Users;

public static class PermissionErrors
{
    public static Error NotFound = new(
        "Permission.NotFound",
        "The permission with the specified identifier was not found");

    public static Error AlreadyExists = new(
        "Permission.AlreadyExists",
        "A permission with this name already exists");

    public static Error InvalidName = new(
        "Permission.InvalidName",
        "Permission name must follow 'resource.action' format");

    public static Error InUse = new(
        "Permission.InUse",
        "Cannot delete permission because it is assigned to one or more roles");

    public static Error SystemPermission = new(
        "Permission.SystemPermission",
        "Cannot modify or delete system permissions");
}
