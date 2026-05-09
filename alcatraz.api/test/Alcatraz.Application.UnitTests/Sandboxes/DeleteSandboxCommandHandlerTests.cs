using FluentAssertions;
using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Clock;
using Alcatraz.Application.Sandboxes.DeleteSandbox;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes;
using NSubstitute;

namespace Alcatraz.Application.UnitTests.Sandboxes;

public class DeleteSandboxCommandHandlerTests
{
    private static readonly DateTime UtcNow = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OwnerUserId = Guid.NewGuid();

    private readonly ISandboxRepository _sandboxRepository = Substitute.For<ISandboxRepository>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly DeleteSandboxCommandHandler _handler;

    public DeleteSandboxCommandHandlerTests()
    {
        var clock = Substitute.For<IDateTimeProvider>();
        clock.UtcNow.Returns(UtcNow);
        _userContext.UserId.Returns(OwnerUserId);
        _handler = new DeleteSandboxCommandHandler(_sandboxRepository, _userContext, _unitOfWork, clock);
    }

    [Fact]
    public async Task Handle_WhenSandboxNotFound_ReturnsNotFound()
    {
        var sandboxId = Guid.NewGuid();
        _sandboxRepository.GetByIdAsync(sandboxId, Arg.Any<CancellationToken>()).Returns((Sandbox?)null);

        var result = await _handler.Handle(new DeleteSandboxCommand(sandboxId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxErrors.NotFound);
    }

    [Fact]
    public async Task Handle_WhenOwnedByDifferentUser_ReturnsNotFound()
    {
        var sandbox = Sandbox.Request(Guid.NewGuid(), 2, 2048, UtcNow);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await _handler.Handle(new DeleteSandboxCommand(sandbox.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxErrors.NotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOwned_MarksDeletingAndSaves()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await _handler.Handle(new DeleteSandboxCommand(sandbox.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        sandbox.Status.Should().Be(SandboxStatus.Deleting);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenAlreadyDeleting_ReturnsAlreadyDeleting()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        sandbox.MarkDeleting(UtcNow);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await _handler.Handle(new DeleteSandboxCommand(sandbox.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxErrors.AlreadyDeleting);
    }
}
