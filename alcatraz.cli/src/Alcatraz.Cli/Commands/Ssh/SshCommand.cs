using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Cli;
using Alcatraz.Cli.Common.Configuration;
using Alcatraz.Cli.Common.Ssh;
using Microsoft.Extensions.Options;
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
    public override Task<int> ExecuteAsync(CommandContext context, SshSettings settings) =>
        runner.RunAsync(async ct =>
        {
            _ = await api.GetSandboxAsync(settings.SandboxId, ct);

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
                settings.RemoteCommand);

            return await launcher.RunAsync(inv, ct);
        });
}
