using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Cli;
using Alcatraz.Cli.Common.Configuration;
using Alcatraz.Cli.Common.Ssh;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Sandboxes.IssueSshCertificate;

internal sealed class IssueSshCertificateCommand(
    IAlcatrazApiClient api,
    ISshKeyManager keys,
    ICertificateCache cache,
    CommandRunner runner) : AsyncCommand<IssueSshCertificateSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, IssueSshCertificateSettings settings) =>
        runner.RunAsync(async ct =>
        {
            var pubKeyPath = settings.PublicKeyPath
                ?? (await keys.EnsureKeyPairAsync(ct)).PublicPath;

            var pubKey = await keys.ReadPublicKeyAsync(pubKeyPath, ct);
            var cert = await api.IssueSshCertificateAsync(settings.SandboxId, pubKey, ct);

            var outPath = settings.OutputPath
                ?? ConfigPathResolver.GetCertPath(settings.SandboxId);

            if (settings.OutputPath is null)
            {
                await cache.SaveAsync(settings.SandboxId, cert.Cert, cert.ValidUntilUtc, ct);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                await File.WriteAllTextAsync(outPath, cert.Cert, ct);
            }

            AnsiConsole.MarkupLineInterpolated($"[green]Wrote cert[/] to [bold]{outPath}[/]");
            AnsiConsole.MarkupLineInterpolated($"Valid until: [yellow]{cert.ValidUntilUtc:u}[/]");
            AnsiConsole.MarkupLineInterpolated($"Gateway: [cyan]{cert.GatewayHost}:{cert.GatewayPort}[/]");
            return ExitCodes.Ok;
        });
}
