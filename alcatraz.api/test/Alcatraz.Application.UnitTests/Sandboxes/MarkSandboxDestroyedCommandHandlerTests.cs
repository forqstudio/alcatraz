using FluentAssertions;
using Alcatraz.Application.Abstractions.Clock;
using Alcatraz.Application.Sandboxes.MarkSandboxDestroyed;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Alcatraz.Application.UnitTests.Sandboxes;

public class MarkSandboxDestroyedCommandHandlerTests
{
    private static readonly DateTime UtcNow = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OwnerUserId = Guid.NewGuid();

    private readonly ISandboxRepository _sandboxRepository = Substitute.For<ISandboxRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MarkSandboxDestroyedCommandHandler _handler;

    public MarkSandboxDestroyedCommandHandlerTests()
    {
        var clock = Substitute.For<IDateTimeProvider>();
        clock.UtcNow.Returns(UtcNow);
        _handler = new MarkSandboxDestroyedCommandHandler(
            _sandboxRepository, _unitOfWork, clock, NullLogger<MarkSandboxDestroyedCommandHandler>.Instance);
    }

    private static SandboxRuntimeInfo Runtime() =>
        new(
            Host: "172.16.0.10",
            Port: 22,
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
            VmIp: "172.16.0.10",
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

        var result = await _handler.Handle(new MarkSandboxDestroyedCommand(sandboxId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxErrors.NotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDeleting_TransitionsToDeleted_AndSaves()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.MarkDeleting(UtcNow);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await _handler.Handle(new MarkSandboxDestroyedCommand(sandbox.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        sandbox.Status.Should().Be(SandboxStatus.Deleted);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenRunning_TransitionsToFailed_AndSaves()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.MarkRunning(Runtime(), UtcNow);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await _handler.Handle(new MarkSandboxDestroyedCommand(sandbox.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        sandbox.Status.Should().Be(SandboxStatus.Failed);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenAlreadyDeleted_NoSave_NoError()
    {
        // At-least-once delivery: a replayed vm.destroyed for a sandbox already in Deleted
        // must succeed without re-saving (idempotent).
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.MarkDeleting(UtcNow);
        sandbox.MarkDestroyed(UtcNow);
        sandbox.Status.Should().Be(SandboxStatus.Deleted);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await _handler.Handle(new MarkSandboxDestroyedCommand(sandbox.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
