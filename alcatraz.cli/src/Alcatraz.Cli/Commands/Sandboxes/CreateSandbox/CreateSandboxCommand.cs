using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Cli;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Sandboxes.CreateSandbox;

internal sealed class CreateSandboxCommand(IAlcatrazApiClient api, CommandRunner runner)
    : AsyncCommand<CreateSandboxSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, CreateSandboxSettings settings) =>
        runner.RunAsync(async ct =>
        {
            var sandbox = await api.CreateSandboxAsync(settings.Vcpus, settings.MemoryMib, ct);
            SandboxRenderer.Render(sandbox, settings.Json);
            return ExitCodes.Ok;
        });
}
