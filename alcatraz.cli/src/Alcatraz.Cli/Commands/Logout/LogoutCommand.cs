using Alcatraz.Cli.Common.Authentication;
using Alcatraz.Cli.Common.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Logout;

internal sealed class LogoutCommand(ITokenStore tokens) : Command<GlobalSettings>
{
    public override int Execute(CommandContext context, GlobalSettings settings)
    {
        tokens.Clear();
        AnsiConsole.MarkupLine("[green]Logged out.[/]");
        return ExitCodes.Ok;
    }
}
