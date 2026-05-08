namespace Alcatraz.Cli.Commands.Login;

public enum DeviceTokenError
{
    None,
    AuthorizationPending,
    SlowDown,
    ExpiredToken,
    AccessDenied,
    Other,
}
