using System.Diagnostics;
using Alcatraz.Cli.Common.Configuration;

namespace Alcatraz.Cli.Commands.Ssh;

public sealed record SshInvocation(
    string PrivateKeyPath,
    string CertPath,
    string GatewayHost,
    int GatewayPort,
    bool UseProxy,
    string? RemoteCommand);

public interface ISshLauncher
{
    IReadOnlyList<string> BuildArgs(SshInvocation inv);
    Task<int> RunAsync(SshInvocation inv, CancellationToken ct = default);
}

internal sealed class SshLauncher : ISshLauncher
{
    public IReadOnlyList<string> BuildArgs(SshInvocation inv)
    {
        var args = new List<string>
        {
            "-i", inv.PrivateKeyPath,
            "-o", $"CertificateFile={inv.CertPath}",
            "-o", "IdentitiesOnly=yes",
            "-o", $"UserKnownHostsFile={ConfigPathResolver.GetKnownHostsPath()}",
            "-o", "StrictHostKeyChecking=accept-new",
        };

        if (inv.UseProxy)
        {
            args.Add("-o");
            args.Add($"ProxyCommand=openssl s_client -quiet -connect {inv.GatewayHost}:{inv.GatewayPort}");
        }

        args.Add("-p");
        args.Add(inv.GatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        args.Add($"al@{inv.GatewayHost}");

        if (!string.IsNullOrWhiteSpace(inv.RemoteCommand))
        {
            args.Add(inv.RemoteCommand);
        }

        return args;
    }

    public async Task<int> RunAsync(SshInvocation inv, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("ssh") { UseShellExecute = false };
        foreach (var arg in BuildArgs(inv))
        {
            psi.ArgumentList.Add(arg);
        }

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ssh");
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }
}
