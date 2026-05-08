using ForqStudio.Application.Abstractions.Caching;
using ForqStudio.Application.Roles.RemovePermissions;
using ForqStudio.Application.UnitTests.Permissions;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Roles;

public class RemovePermissionsTests
{
    private readonly RemovePermissionsCommandHandler _handler;
    private readonly IRoleRepository _roleRepositoryMock;
    private readonly ICacheService _cacheServiceMock;
    private readonly IUnitOfWork _unitOfWorkMock;

    public RemovePermissionsTests()
    {
        _roleRepositoryMock = Substitute.For<IRoleRepository>();
        _cacheServiceMock = Substitute.For<ICacheService>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();

        _handler = new RemovePermissionsCommandHandler(
            _roleRepositoryMock,
            _cacheServiceMock,
            _unitOfWorkMock);
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenRoleNotFound()
    {
        // Arrange
        var command = new RemovePermissionsCommand(100, new List<int> { 1 });

        _roleRepositoryMock
            .GetByIdAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(RoleErrors.NotFound);
    }

    [Fact]
    public async Task Handle_Should_ReturnSuccess_WhenPermissionsRemoved()
    {
        // Arrange
        var permission = PermissionData.Create(1, "users.read");
        var role = RoleData.CreateWithPermissions(100, "testrole", permission);
        var command = new RemovePermissionsCommand(100, new List<int> { 1 });

        _roleRepositoryMock
            .GetByIdAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_Should_RemovePermissionsFromRole_WhenSuccessful()
    {
        // Arrange
        var permission1 = PermissionData.Create(1, "users.read");
        var permission2 = PermissionData.Create(2, "users.write");
        var role = RoleData.CreateWithPermissions(100, "testrole", permission1, permission2);
        var command = new RemovePermissionsCommand(100, new List<int> { 1 });

        _roleRepositoryMock
            .GetByIdAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        await _handler.Handle(command, default);

        // Assert
        role.Permissions.Should().HaveCount(1);
        role.Permissions.Should().NotContain(p => p.Id == 1);
        role.Permissions.Should().Contain(p => p.Id == 2);
    }

    [Fact]
    public async Task Handle_Should_RemoveMultiplePermissions_WhenSuccessful()
    {
        // Arrange
        var permission1 = PermissionData.Create(1, "users.read");
        var permission2 = PermissionData.Create(2, "users.write");
        var permission3 = PermissionData.Create(3, "bookings.read");
        var role = RoleData.CreateWithPermissions(100, "testrole", permission1, permission2, permission3);
        var command = new RemovePermissionsCommand(100, new List<int> { 1, 2 });

        _roleRepositoryMock
            .GetByIdAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        await _handler.Handle(command, default);

        // Assert
        role.Permissions.Should().HaveCount(1);
        role.Permissions.Should().Contain(p => p.Id == 3);
    }

    [Fact]
    public async Task Handle_Should_CallUnitOfWork_WhenSuccessful()
    {
        // Arrange
        var permission = PermissionData.Create(1, "users.read");
        var role = RoleData.CreateWithPermissions(100, "testrole", permission);
        var command = new RemovePermissionsCommand(100, new List<int> { 1 });

        _roleRepositoryMock
            .GetByIdAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        await _handler.Handle(command, default);

        // Assert
        await _unitOfWorkMock.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_InvalidateCaches_WhenSuccessful()
    {
        // Arrange
        var permission = PermissionData.Create(1, "users.read");
        var role = RoleData.CreateWithPermissions(100, "testrole", permission);
        var command = new RemovePermissionsCommand(100, new List<int> { 1 });
        var identityIds = new List<string> { "identity1", "identity2" };

        _roleRepositoryMock
            .GetByIdAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.RoleId, Arg.Any<CancellationToken>())
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

    [Fact]
    public async Task Handle_Should_NotFail_WhenRemovingNonExistentPermissionId()
    {
        // Arrange
        var permission = PermissionData.Create(1, "users.read");
        var role = RoleData.CreateWithPermissions(100, "testrole", permission);
        var command = new RemovePermissionsCommand(100, new List<int> { 999 });

        _roleRepositoryMock
            .GetByIdAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
        role.Permissions.Should().HaveCount(1);
    }
}
