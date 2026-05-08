using System.ComponentModel;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Common.Cli;

public class GlobalSettings : CommandSettings
{
    [CommandOption("--api-url <URL>")]
    [Description("Override the API base URL (default from config or http://localhost:8080)")]
    public string? ApiBaseUrl { get; init; }

    [CommandOption("--json")]
    [Description("Emit machine-readable JSON instead of formatted tables")]
    public bool Json { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Enable verbose logging")]
    public bool Verbose { get; init; }
}
