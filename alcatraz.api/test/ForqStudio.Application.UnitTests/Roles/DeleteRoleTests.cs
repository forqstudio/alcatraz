using ForqStudio.Application.Abstractions.Caching;
using ForqStudio.Application.Roles.DeleteRole;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Roles;

public class DeleteRoleTests
{
    private readonly DeleteRoleCommandHandler _handler;
    private readonly IRoleRepository _roleRepositoryMock;
    private readonly ICacheService _cacheServiceMock;
    private readonly IUnitOfWork _unitOfWorkMock;

    public DeleteRoleTests()
    {
        _roleRepositoryMock = Substitute.For<IRoleRepository>();
        _cacheServiceMock = Substitute.For<ICacheService>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();

        _handler = new DeleteRoleCommandHandler(
            _roleRepositoryMock,
            _cacheServiceMock,
            _unitOfWorkMock);
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenRoleNotFound()
    {
        // Arrange
        var command = new DeleteRoleCommand(100);

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(RoleErrors.NotFound);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task Handle_Should_ReturnFailure_WhenRoleIsSystemRole(int systemRoleId)
    {
        // Arrange
        var command = new DeleteRoleCommand(systemRoleId);
        var systemRole = RoleData.CreateSystemRole(systemRoleId);

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(systemRole);

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(RoleErrors.SystemRole);
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenRoleIsInUse()
    {
        // Arrange
        var command = new DeleteRoleCommand(100);
        var role = RoleData.Create(100);

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .IsInUseAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(RoleErrors.InUse);
    }

    [Fact]
    public async Task Handle_Should_ReturnSuccess_WhenRoleIsDeleted()
    {
        // Arrange
        var command = new DeleteRoleCommand(100);
        var role = RoleData.Create(100);

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .IsInUseAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(false);

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_Should_CallRepositoryDelete_WhenRoleIsDeleted()
    {
        // Arrange
        var command = new DeleteRoleCommand(100);
        var role = RoleData.Create(100);

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .IsInUseAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(false);

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        await _handler.Handle(command, default);

        // Assert
        _roleRepositoryMock.Received(1).Delete(role);
    }

    [Fact]
    public async Task Handle_Should_InvalidateCaches_WhenRoleIsDeleted()
    {
        // Arrange
        var command = new DeleteRoleCommand(100);
        var role = RoleData.Create(100);
        var identityIds = new List<string> { "identity1", "identity2" };

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .IsInUseAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(false);

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(identityIds);

        // Act
        await _handler.Handle(command, default);

        // Assert
        await _cacheServiceMock.Received(1).RemoveManyAsync(
            Arg.Is<IEnumerable<string>>(keys =>
                keys.Contains("auth:permissions-identity1") &&
                keys.Contains("auth:permissions-identity2") &&
                keys.Contains("auth:roles-identity1") &&
                keys.Contains("auth:roles-identity2")),
            Arg.Any<CancellationToken>());
    }
}
