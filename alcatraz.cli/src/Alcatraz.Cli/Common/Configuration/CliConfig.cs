namespace Alcatraz.Cli.Common.Configuration;

public sealed record CliConfig(string ApiBaseUrl, bool AlwaysUseGatewayProxy)
{
    public static CliConfig Default { get; } = new("http://localhost:8080", false);
}
