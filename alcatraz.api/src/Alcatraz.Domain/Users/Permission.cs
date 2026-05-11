using System.Text.RegularExpressions;

namespace Alcatraz.Domain.Users;

public sealed class Permission
{
    public static readonly Permission UsersRead = new(1, "users.read");
    public static readonly Permission UsersWrite = new(6, "users.write");
    public static readonly Permission SandboxesRead = new(7, "sandboxes.read");
    public static readonly Permission SandboxesWrite = new(8, "sandboxes.write");
    public static readonly Permission SandboxesSsh = new(9, "sandboxes.ssh");

    public const int MaxSystemId = 10;
    public const int NameMaxLength = 100;
    public const string NameFormatMessage = "Permission name must follow 'resource.action' format with lowercase letters only";

    public const string NameRegexPattern = @"^[a-z]+\.[a-z]+$";

    private static readonly Regex NameRegex = new(NameRegexPattern, RegexOptions.Compiled);

    public int Id { get; init; }

    public string Name { get; private set; }

    public static bool IsValidName(string name) => NameRegex.IsMatch(name);

    public bool IsDeleted { get; private set; }

    public Permission(int id, string name)
    {
        Id = id;
        Name = name;
    }

    public static Permission Create(int id, string name)
    {
        return new Permission(id, name);
    }

    public void UpdateName(string name)
    {
        Name = name;
    }

    public void MarkAsDeleted()
    {
        IsDeleted = true;
    }
}
