using System.ComponentModel;
using Alcatraz.Cli.Common.Cli;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Login;

public sealed class LoginSettings : GlobalSettings
{
    [CommandOption("--no-browser")]
    [Description("Print the verification URL instead of auto-opening a browser")]
    public bool NoBrowser { get; init; }
}
