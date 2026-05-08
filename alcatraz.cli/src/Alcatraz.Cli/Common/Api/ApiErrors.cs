namespace Alcatraz.Cli.Common.Api;

public class AlcatrazCliException : Exception
{
    protected AlcatrazCliException(string message) : base(message) { }
    protected AlcatrazCliException(string message, Exception inner) : base(message, inner) { }
}

public sealed class NotLoggedInException : AlcatrazCliException
{
    public NotLoggedInException()
        : base("Not logged in. Run `alcatraz login` first.") { }
}

public sealed class ExpiredDeviceCodeException : AlcatrazCliException
{
    public ExpiredDeviceCodeException()
        : base("The device code has expired. Run `alcatraz login` again.") { }
}

public sealed class AuthorizationDeniedException : AlcatrazCliException
{
    public AuthorizationDeniedException()
        : base("Authorization was denied in the browser.") { }
}

public sealed class SandboxNotFoundException : AlcatrazCliException
{
    public Guid SandboxId { get; }

    public SandboxNotFoundException(Guid sandboxId)
        : base($"Sandbox {sandboxId} was not found, or you don't have access to it.")
    {
        SandboxId = sandboxId;
    }
}

public sealed class BadRequestException : AlcatrazCliException
{
    public BadRequestException(string detail)
        : base($"Bad request: {detail}") { }
}

public sealed class ConflictException : AlcatrazCliException
{
    public ConflictException(string detail)
        : base($"Conflict: {detail}") { }
}

public sealed class ApiUnavailableException : AlcatrazCliException
{
    public ApiUnavailableException(string detail)
        : base($"The alcatraz API is unavailable: {detail}") { }

    public ApiUnavailableException(string detail, Exception inner)
        : base($"The alcatraz API is unavailable: {detail}", inner) { }
}
