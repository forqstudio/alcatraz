namespace Alcatraz.Cli.Common.Configuration;

public sealed class CliOptions
{
    public string ApiBaseUrl { get; set; } = "http://localhost:8080";

    public bool AlwaysUseGatewayProxy { get; set; }
}
