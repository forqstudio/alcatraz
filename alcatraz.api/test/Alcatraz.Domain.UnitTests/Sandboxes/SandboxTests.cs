using FluentAssertions;
using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Sandboxes.Events;
using Alcatraz.Domain.UnitTests.Infrastructure;

namespace Alcatraz.Domain.UnitTests.Sandboxes;

public class SandboxTests : BaseTest
{
    private static readonly DateTime UtcNow = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OwnerUserId = Guid.NewGuid();

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
    public void Request_SetsProvisioningStatus_AndRaisesSandboxRequestedEvent()
    {
        var sandbox = Sandbox.Request(OwnerUserId, vcpus: 4, memoryMib: 4096, UtcNow);

        sandbox.Status.Should().Be(SandboxStatus.Provisioning);
        sandbox.OwnerUserId.Should().Be(OwnerUserId);
        sandbox.RequestedVcpus.Should().Be(4);
        sandbox.RequestedMemoryMib.Should().Be(4096);
        sandbox.CreatedOnUtc.Should().Be(UtcNow);
        sandbox.DeletedOnUtc.Should().BeNull();

        var raised = AssertDomainEventWasPublished<SandboxRequestedDomainEvent>(sandbox);
        raised.SandboxId.Should().Be(sandbox.Id);
        raised.OwnerUserId.Should().Be(OwnerUserId);
        raised.Vcpus.Should().Be(4);
        raised.MemoryMib.Should().Be(4096);
    }

    [Fact]
    public void MarkDeleting_FromProvisioning_TransitionsToDeleting_AndRaisesEvent()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.ClearDomainEvents();

        var result = sandbox.MarkDeleting(UtcNow.AddMinutes(5));

        result.IsSuccess.Should().BeTrue();
        sandbox.Status.Should().Be(SandboxStatus.Deleting);
        sandbox.DeletedOnUtc.Should().Be(UtcNow.AddMinutes(5));

        AssertDomainEventWasPublished<SandboxDeletionRequestedDomainEvent>(sandbox)
            .SandboxId.Should().Be(sandbox.Id);
    }

    [Fact]
    public void MarkDeleting_WhenAlreadyDeleting_ReturnsAlreadyDeleting()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.MarkDeleting(UtcNow);
        sandbox.ClearDomainEvents();

        var result = sandbox.MarkDeleting(UtcNow.AddSeconds(1));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxErrors.AlreadyDeleting);
        sandbox.DomainEvents().Should().BeEmpty();
    }

    [Fact]
    public void EnsureOwnedBy_WithMatchingUser_ReturnsSuccess()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);

        var result = sandbox.EnsureOwnedBy(OwnerUserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureOwnedBy_WithDifferentUser_ReturnsNotFound()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);

        var result = sandbox.EnsureOwnedBy(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxErrors.NotFound);
    }

    [Fact]
    public void CanIssueCertificate_OnlyTrue_ForRunning()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        // Provisioning: cert issuance now requires the worker to have reported the
        // sandbox endpoint via vm.ready, which advances the state to Running.
        sandbox.CanIssueCertificate().Should().BeFalse();

        sandbox.MarkRunning(Runtime(), UtcNow);
        sandbox.CanIssueCertificate().Should().BeTrue();

        sandbox.MarkDeleting(UtcNow);
        sandbox.CanIssueCertificate().Should().BeFalse();
    }

    [Fact]
    public void MarkRunning_FromProvisioning_TransitionsToRunning_StoresEndpoint_AndRaisesEvent()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.ClearDomainEvents();

        var result = sandbox.MarkRunning(Runtime(), UtcNow.AddSeconds(5));

        result.IsSuccess.Should().BeTrue();
        sandbox.Status.Should().Be(SandboxStatus.Running);
        sandbox.Host.Should().Be("172.16.0.10");
        sandbox.Port.Should().Be(22);
        sandbox.ActualVcpus.Should().Be(2);
        sandbox.ActualMemoryMib.Should().Be(2048);
        sandbox.BootDurationMs.Should().Be(1234);
        sandbox.VmmVersion.Should().Be("1.15.1");
        sandbox.TapDevice.Should().Be("fc-tap0");

        AssertDomainEventWasPublished<SandboxBecameRunningDomainEvent>(sandbox)
            .SandboxId.Should().Be(sandbox.Id);
    }

    [Fact]
    public void MarkRunning_WhenNotProvisioning_ReturnsNotProvisioningError()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.MarkRunning(Runtime(), UtcNow);
        sandbox.ClearDomainEvents();

        var result = sandbox.MarkRunning(Runtime(host: "172.16.0.11"), UtcNow.AddSeconds(1));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxErrors.NotProvisioning);
        sandbox.Host.Should().Be("172.16.0.10");
        sandbox.DomainEvents().Should().BeEmpty();
    }

    [Fact]
    public void MarkDeleting_FromRunning_TransitionsToDeleting_AndRaisesEvent()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.MarkRunning(Runtime(), UtcNow.AddSeconds(5));
        sandbox.ClearDomainEvents();

        var result = sandbox.MarkDeleting(UtcNow.AddMinutes(10));

        result.IsSuccess.Should().BeTrue();
        sandbox.Status.Should().Be(SandboxStatus.Deleting);
        sandbox.DeletedOnUtc.Should().Be(UtcNow.AddMinutes(10));

        AssertDomainEventWasPublished<SandboxDeletionRequestedDomainEvent>(sandbox)
            .SandboxId.Should().Be(sandbox.Id);
    }

    [Fact]
    public void MarkDeleting_WhenAlreadyDeleted_ReturnsAlreadyDeleted()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.MarkDeleting(UtcNow);
        sandbox.MarkDestroyed(UtcNow.AddMinutes(1));
        sandbox.Status.Should().Be(SandboxStatus.Deleted);
        sandbox.ClearDomainEvents();

        var result = sandbox.MarkDeleting(UtcNow.AddMinutes(2));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxErrors.AlreadyDeleted);
        sandbox.DomainEvents().Should().BeEmpty();
    }

    [Fact]
    public void MarkDestroyed_FromDeleting_TransitionsToDeleted()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        var deletingAt = UtcNow.AddMinutes(5);
        sandbox.MarkDeleting(deletingAt);
        sandbox.ClearDomainEvents();

        var result = sandbox.MarkDestroyed(UtcNow.AddMinutes(6));

        result.IsSuccess.Should().BeTrue();
        sandbox.Status.Should().Be(SandboxStatus.Deleted);
        sandbox.DeletedOnUtc.Should().Be(deletingAt);
        sandbox.DomainEvents().Should().BeEmpty();
    }

    [Fact]
    public void MarkDestroyed_FromProvisioning_TransitionsToFailed()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.ClearDomainEvents();

        var result = sandbox.MarkDestroyed(UtcNow.AddSeconds(30));

        result.IsSuccess.Should().BeTrue();
        sandbox.Status.Should().Be(SandboxStatus.Failed);
        sandbox.DomainEvents().Should().BeEmpty();
    }

    [Fact]
    public void MarkDestroyed_FromRunning_TransitionsToFailed()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.MarkRunning(Runtime(), UtcNow.AddSeconds(5));
        sandbox.ClearDomainEvents();

        var result = sandbox.MarkDestroyed(UtcNow.AddMinutes(2));

        result.IsSuccess.Should().BeTrue();
        sandbox.Status.Should().Be(SandboxStatus.Failed);
        sandbox.DomainEvents().Should().BeEmpty();
    }

    [Fact]
    public void MarkDestroyed_WhenAlreadyDeleted_IsNoOp()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.MarkDeleting(UtcNow);
        sandbox.MarkDestroyed(UtcNow.AddMinutes(1));
        sandbox.Status.Should().Be(SandboxStatus.Deleted);
        var deletedAt = sandbox.DeletedOnUtc;
        sandbox.ClearDomainEvents();

        var result = sandbox.MarkDestroyed(UtcNow.AddMinutes(5));

        result.IsSuccess.Should().BeTrue();
        sandbox.Status.Should().Be(SandboxStatus.Deleted);
        sandbox.DeletedOnUtc.Should().Be(deletedAt);
        sandbox.DomainEvents().Should().BeEmpty();
    }

    [Fact]
    public void MarkDestroyed_WhenAlreadyFailed_IsNoOp()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.MarkDestroyed(UtcNow.AddSeconds(10));
        sandbox.Status.Should().Be(SandboxStatus.Failed);
        sandbox.ClearDomainEvents();

        var result = sandbox.MarkDestroyed(UtcNow.AddMinutes(5));

        result.IsSuccess.Should().BeTrue();
        sandbox.Status.Should().Be(SandboxStatus.Failed);
        sandbox.DomainEvents().Should().BeEmpty();
    }
}
