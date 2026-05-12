using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Cli;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Sandboxes.Usage;

internal sealed class UsageCommand(IAlcatrazApiClient api, CommandRunner runner)
    : AsyncCommand<UsageSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, UsageSettings settings) =>
        runner.RunAsync(async ct =>
        {
            if (settings.SandboxId is { } id)
            {
                var usage = await api.GetSandboxUsageAsync(id, ct);
                UsageRenderer.Render(usage, settings.Json);
            }
            else
            {
                var usages = await api.ListSandboxUsageAsync(ct);
                UsageRenderer.RenderList(usages, settings.Json);
            }
            return ExitCodes.Ok;
        });
}
