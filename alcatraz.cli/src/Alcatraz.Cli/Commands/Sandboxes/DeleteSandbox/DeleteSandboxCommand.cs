using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Sandboxes.DeleteSandbox;

internal sealed class DeleteSandboxCommand(IAlcatrazApiClient api, CommandRunner runner)
    : AsyncCommand<SandboxIdSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, SandboxIdSettings settings) =>
        runner.RunAsync(async ct =>
        {
            await api.DeleteSandboxAsync(settings.SandboxId, ct);
            AnsiConsole.MarkupLineInterpolated($"[green]Marked sandbox {settings.SandboxId} for deletion.[/]");
            return ExitCodes.Ok;
        });
}
