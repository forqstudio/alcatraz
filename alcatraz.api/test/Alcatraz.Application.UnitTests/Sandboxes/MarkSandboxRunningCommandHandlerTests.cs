using FluentAssertions;
using Alcatraz.Application.Abstractions.Clock;
using Alcatraz.Application.Sandboxes.MarkSandboxRunning;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Alcatraz.Application.UnitTests.Sandboxes;

public class MarkSandboxRunningCommandHandlerTests
{
    private static readonly DateTime UtcNow = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OwnerUserId = Guid.NewGuid();

    private readonly ISandboxRepository _sandboxRepository = Substitute.For<ISandboxRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MarkSandboxRunningCommandHandler _handler;

    public MarkSandboxRunningCommandHandlerTests()
    {
        var clock = Substitute.For<IDateTimeProvider>();
        clock.UtcNow.Returns(UtcNow);
        _handler = new MarkSandboxRunningCommandHandler(
            _sandboxRepository, _unitOfWork, clock, NullLogger<MarkSandboxRunningCommandHandler>.Instance);
    }

    private static SandboxRuntimeInfo Runtime(string host = "172.16.0.10", int port = 22) =>
        new(
            Host: host,
            Port: port,
            ActualVcpus: 2,
            ActualMemoryMib: 2048,
            BootDurationMs: 1234,
            ReadyAtUtc: UtcNow,
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
    public async Task Handle_WhenSandboxNotFound_ReturnsNotFound()
    {
        var sandboxId = Guid.NewGuid();
        _sandboxRepository.GetByIdAsync(sandboxId, Arg.Any<CancellationToken>()).Returns((Sandbox?)null);

        var result = await _handler.Handle(
            new MarkSandboxRunningCommand(sandboxId, Runtime()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxErrors.NotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenAlreadyRunningWithSameEndpoint_ReturnsSuccess_WithoutSaving()
    {
        // At-least-once delivery may replay the same vm.ready: same host:port should be a no-op.
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.MarkRunning(Runtime(), UtcNow);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await _handler.Handle(
            new MarkSandboxRunningCommand(sandbox.Id, Runtime()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSandboxNotProvisioning_PropagatesNotProvisioningError()
    {
        // A non-replay vm.ready that lands on a sandbox already past Provisioning (e.g. Failed)
        // should surface the domain's NotProvisioning error rather than transition.
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.MarkDestroyed(UtcNow);
        sandbox.Status.Should().Be(SandboxStatus.Failed);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await _handler.Handle(
            new MarkSandboxRunningCommand(sandbox.Id, Runtime()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxErrors.NotProvisioning);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenProvisioning_TransitionsAndSaves()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await _handler.Handle(
            new MarkSandboxRunningCommand(sandbox.Id, Runtime()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        sandbox.Status.Should().Be(SandboxStatus.Running);
        sandbox.Host.Should().Be("172.16.0.10");
        sandbox.Port.Should().Be(22);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
