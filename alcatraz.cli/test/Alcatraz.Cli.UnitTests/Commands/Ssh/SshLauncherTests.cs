using Alcatraz.Cli.Commands.Ssh;
using FluentAssertions;

namespace Alcatraz.Cli.UnitTests.Commands.Ssh;

public class SshLauncherTests
{
    private readonly SshLauncher launcher = new();

    [Fact]
    public void BuildArgs_LocalDemoForm_NoProxyCommand()
    {
        var inv = new SshInvocation(
            PrivateKeyPath: "/p/id",
            CertPath: "/p/cert",
            GatewayHost: "localhost",
            GatewayPort: 2222,
            UseProxy: false,
            RemoteCommand: null);

        var args = launcher.BuildArgs(inv);

        args.Should().NotContain(a => a.StartsWith("ProxyCommand="));
        args.Should().Contain("-p");
        args.Should().Contain("2222");
        args.Should().Contain("al@localhost");
        args.Should().Contain("CertificateFile=/p/cert");
    }

    [Fact]
    public void BuildArgs_ProductionForm_HasProxyCommand()
    {
        var inv = new SshInvocation(
            PrivateKeyPath: "/p/id",
            CertPath: "/p/cert",
            GatewayHost: "ssh.alcatraz.io",
            GatewayPort: 443,
            UseProxy: true,
            RemoteCommand: null);

        var args = launcher.BuildArgs(inv);

        args.Should().Contain("ProxyCommand=openssl s_client -quiet -connect ssh.alcatraz.io:443");
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
            GatewayPort: 2222,
            UseProxy: false,
            RemoteCommand: "echo hello");

        var args = launcher.BuildArgs(inv);

        args.Last().Should().Be("echo hello");
    }
}
