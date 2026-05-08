using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Cli;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Sandboxes.GetSandbox;

internal sealed class GetSandboxCommand(IAlcatrazApiClient api, CommandRunner runner)
    : AsyncCommand<SandboxIdSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, SandboxIdSettings settings) =>
        runner.RunAsync(async ct =>
        {
            var sandbox = await api.GetSandboxAsync(settings.SandboxId, ct);
            SandboxRenderer.Render(sandbox, settings.Json);
            return ExitCodes.Ok;
        });
}
