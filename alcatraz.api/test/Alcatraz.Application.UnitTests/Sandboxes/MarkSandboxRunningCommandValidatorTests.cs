using FluentAssertions;
using Alcatraz.Application.Sandboxes.MarkSandboxRunning;
using Alcatraz.Domain.Sandboxes;

namespace Alcatraz.Application.UnitTests.Sandboxes;

public class MarkSandboxRunningCommandValidatorTests
{
    private readonly MarkSandboxRunningCommandValidator _validator = new();

    private static SandboxRuntimeInfo ValidRuntime(string host = "172.16.0.10", int port = 22) =>
        new(
            Host: host,
            Port: port,
            ActualVcpus: 2,
            ActualMemoryMib: 2048,
            BootDurationMs: 1234,
            ReadyAtUtc: DateTime.UtcNow,
            VmmVersion: "1.15.1",
            VmmState: "Running",
            FirecrackerPid: 4242,
            SocketPath: "/tmp/alcatraz-test.sock",
            TapDevice: "fc-tap0",
            MacAddress: "AA:FC:00:00:00:01",
            VmIp: host,
            HostGatewayIp: "172.16.0.1",
            NfsPort: 8000,
            WorkerSlotIndex: 0,
            RootfsPath: "/test/rootfs",
            KernelPath: "/test/vmlinux");

    [Fact]
    public void Validate_WhenAllValid_Succeeds()
    {
        var result = _validator.Validate(new MarkSandboxRunningCommand(Guid.NewGuid(), ValidRuntime()));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenSandboxIdEmpty_Fails()
    {
        var result = _validator.Validate(new MarkSandboxRunningCommand(Guid.Empty, ValidRuntime()));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenHostEmpty_Fails()
    {
        var result = _validator.Validate(
            new MarkSandboxRunningCommand(Guid.NewGuid(), ValidRuntime(host: "")));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenHostTooLong_Fails()
    {
        var tooLongHost = new string('a', 256);

        var result = _validator.Validate(
            new MarkSandboxRunningCommand(Guid.NewGuid(), ValidRuntime(host: tooLongHost)));

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_WhenPortOutOfRange_Fails(int port)
    {
        var result = _validator.Validate(new MarkSandboxRunningCommand(Guid.NewGuid(), ValidRuntime(port: port)));

        result.IsValid.Should().BeFalse();
    }
}
