using FluentAssertions;
using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Clock;
using ForqStudio.Application.Sandboxes.CreateSandbox;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Sandboxes;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Sandboxes;

public class CreateSandboxCommandHandlerTests
{
    private static readonly DateTime UtcNow = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly ISandboxRepository _sandboxRepository;
    private readonly IUserContext _userContext;
    private readonly IUnitOfWork _unitOfWork;
    private readonly CreateSandboxCommandHandler _handler;

    public CreateSandboxCommandHandlerTests()
    {
        _sandboxRepository = Substitute.For<ISandboxRepository>();
        _userContext = Substitute.For<IUserContext>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        var clock = Substitute.For<IDateTimeProvider>();
        clock.UtcNow.Returns(UtcNow);

        _userContext.UserId.Returns(UserId);
        _userContext.IdentityId.Returns(Guid.NewGuid().ToString());

        _handler = new CreateSandboxCommandHandler(_sandboxRepository, _userContext, _unitOfWork, clock);
    }

    [Fact]
    public async Task Handle_PersistsSandbox_AndReturnsResponse()
    {
        var command = new CreateSandboxCommand(Vcpus: 2, MemoryMib: 2048);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.OwnerUserId.Should().Be(UserId);
        result.Value.Vcpus.Should().Be(2);
        result.Value.MemoryMib.Should().Be(2048);
        result.Value.Status.Should().Be((int)SandboxStatus.Provisioning);
        result.Value.CreatedOnUtc.Should().Be(UtcNow);

        _sandboxRepository.Received(1).Add(Arg.Is<Sandbox>(s =>
            s.OwnerUserId == UserId &&
            s.RequestedVcpus == 2 &&
            s.RequestedMemoryMib == 2048));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
