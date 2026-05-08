using Alcatraz.Cli.Commands.Ssh;
using FluentAssertions;

namespace Alcatraz.Cli.UnitTests.Commands.Ssh;

public class SshLauncherTests
{
    private readonly SshLauncher launcher = new();

    private static readonly Guid SampleSandboxId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void BuildArgs_LocalDemoForm_NoProxyCommand()
    {
        var inv = new SshInvocation(
            PrivateKeyPath: "/p/id",
            CertPath: "/p/cert",
            GatewayHost: "172.16.0.10",
            GatewayPort: 22,
            UseProxy: false,
            SandboxId: SampleSandboxId,
            RemoteCommand: null);

        var args = launcher.BuildArgs(inv);

        args.Should().NotContain(a => a.StartsWith("ProxyCommand="));
        args.Should().Contain("-p");
        args.Should().Contain("22");
        args.Should().Contain("al@172.16.0.10");
        args.Should().Contain("CertificateFile=/p/cert");
    }

    [Fact]
    public void BuildArgs_ProductionForm_HasProxyCommandWithSni()
    {
        var inv = new SshInvocation(
            PrivateKeyPath: "/p/id",
            CertPath: "/p/cert",
            GatewayHost: "ssh.alcatraz.io",
            GatewayPort: 443,
            UseProxy: true,
            SandboxId: SampleSandboxId,
            RemoteCommand: null);

        var args = launcher.BuildArgs(inv);

        args.Should().Contain(
            $"ProxyCommand=openssl s_client -quiet -connect ssh.alcatraz.io:443 " +
            $"-servername {SampleSandboxId}");
        args.Should().Contain("443");
        args.Should().Contain("al@ssh.alcatraz.io");
    }

    [Fact]
    public void BuildArgs_RemoteCommand_AppendedLast()
    {
        var inv = new SshInvocation(
            PrivateKeyPath: "/p/id",
            CertPath: "/p/cert",
            GatewayHost: "h",
            GatewayPort: 22,
            UseProxy: false,
            SandboxId: SampleSandboxId,
            RemoteCommand: "echo hello");

        var args = launcher.BuildArgs(inv);

        args.Last().Should().Be("echo hello");
    }
}
