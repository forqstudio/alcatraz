using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Bootstrap;
using Alcatraz.Cli.Common.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Login;

internal sealed class LoginCommand(
    IAlcatrazApiClient api,
    IDeviceFlowOrchestrator orchestrator,
    IBrowserLauncher browser,
    CancellationContext cancellation) : AsyncCommand<LoginSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, LoginSettings settings)
    {
        var ct = cancellation.Token;
        try
        {
            var init = await api.InitiateDeviceAuthAsync(ct);

            AnsiConsole.Write(new Panel(new Markup(
                $"""
                Visit [link]{init.VerificationUri}[/]
                and enter the code:

                  [bold yellow]{init.UserCode}[/]

                Or open this pre-filled URL:
                  [link]{init.VerificationUriComplete}[/]
                """))
                .Header(" alcatraz login ")
                .Border(BoxBorder.Rounded));

            if (!settings.NoBrowser)
            {
                browser.TryOpen(init.VerificationUriComplete);
            }

            await AnsiConsole.Status()
                .StartAsync("waiting for browser sign-in…", async _ =>
                {
                    await orchestrator.RunAsync(init, ct);
                });

            AnsiConsole.MarkupLine("[green]Logged in.[/]");
            return ExitCodes.Ok;
        }
        catch (ExpiredDeviceCodeException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return ExitCodes.Generic;
        }
        catch (AuthorizationDeniedException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return ExitCodes.Generic;
        }
        catch (ApiUnavailableException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return ExitCodes.Generic;
        }
    }
}
