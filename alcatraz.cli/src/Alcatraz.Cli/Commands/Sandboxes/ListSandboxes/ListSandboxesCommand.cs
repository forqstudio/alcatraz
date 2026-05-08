using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Cli;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Sandboxes.ListSandboxes;

internal sealed class ListSandboxesCommand(IAlcatrazApiClient api, CommandRunner runner)
    : AsyncCommand<GlobalSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings) =>
        runner.RunAsync(async ct =>
        {
            var sandboxes = await api.ListSandboxesAsync(ct);
            SandboxRenderer.RenderList(sandboxes, settings.Json);
            return ExitCodes.Ok;
        });
}
