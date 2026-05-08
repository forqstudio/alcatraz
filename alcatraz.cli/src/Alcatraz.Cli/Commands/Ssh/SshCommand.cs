using Alcatraz.Cli.Commands.Sandboxes;
using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Cli;
using Alcatraz.Cli.Common.Configuration;
using Alcatraz.Cli.Common.Ssh;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Ssh;

internal sealed class SshCommand(
    IAlcatrazApiClient api,
    ISshKeyManager keys,
    ICertificateCache cache,
    ISshLauncher launcher,
    IOptions<CliOptions> options,
    CommandRunner runner) : AsyncCommand<SshSettings>
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(500);

    public override Task<int> ExecuteAsync(CommandContext context, SshSettings settings) =>
        runner.RunAsync(async ct =>
        {
            // Wait until the worker has reported the sandbox as Running so the
            // API has an endpoint to put in the cert response. Without this,
            // the cert handler would reject with InvalidStateForCertIssue.
            await WaitUntilRunningAsync(settings.SandboxId, ct);

            var (privPath, pubPath) = await keys.EnsureKeyPairAsync(ct);

            // Always re-issue the cert so we get the gateway info each time;
            // 24h cert lifetime makes this nearly free, and the API already
            // returns the gateway endpoint with each issuance.
            var pub = await keys.ReadPublicKeyAsync(pubPath, ct);
            var fresh = await api.IssueSshCertificateAsync(settings.SandboxId, pub, ct);
            await cache.SaveAsync(settings.SandboxId, fresh.Cert, fresh.ValidUntilUtc, ct);

            var certPath = ConfigPathResolver.GetCertPath(settings.SandboxId);
            var gatewayHost = fresh.GatewayHost;
            var gatewayPort = fresh.GatewayPort;

            var useProxy = !settings.NoProxy
                && (options.Value.AlwaysUseGatewayProxy || gatewayPort == 443);

            var inv = new SshInvocation(
                privPath,
                certPath,
                gatewayHost,
                gatewayPort,
                useProxy,
                settings.SandboxId,
                settings.RemoteCommand);

            return await launcher.RunAsync(inv, ct);
        });

    private async Task WaitUntilRunningAsync(Guid sandboxId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyTimeout;
        SandboxResponse? last = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Waiting for sandbox to be ready…", async _ =>
            {
                while (true)
                {
                    last = await api.GetSandboxAsync(sandboxId, ct);
                    if ((SandboxStatus)last.Status == SandboxStatus.Running)
                    {
                        return;
                    }

                    if (DateTime.UtcNow >= deadline)
                    {
                        return;
                    }

                    await Task.Delay(ReadyPollInterval, ct);
                }
            });

        if (last is null || (SandboxStatus)last.Status != SandboxStatus.Running)
        {
            var status = last is null ? "unknown" : ((SandboxStatus)last.Status).ToString();
            throw new SandboxNotReadyException(
                sandboxId,
                $"did not become ready within {ReadyTimeout.TotalSeconds:N0}s (last status: {status}). " +
                "Is the worker running?");
        }
    }
}
