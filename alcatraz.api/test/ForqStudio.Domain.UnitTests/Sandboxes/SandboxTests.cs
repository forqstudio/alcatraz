using FluentAssertions;
using ForqStudio.Domain.Sandboxes;
using ForqStudio.Domain.Sandboxes.Events;
using ForqStudio.Domain.UnitTests.Infrastructure;

namespace ForqStudio.Domain.UnitTests.Sandboxes;

public class SandboxTests : BaseTest
{
    private static readonly DateTime UtcNow = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OwnerUserId = Guid.NewGuid();

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
    public void CanIssueCertificate_OnlyTrue_ForProvisioningOrRunning()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.CanIssueCertificate().Should().BeTrue();

        sandbox.MarkDeleting(UtcNow);
        sandbox.CanIssueCertificate().Should().BeFalse();
    }
}
