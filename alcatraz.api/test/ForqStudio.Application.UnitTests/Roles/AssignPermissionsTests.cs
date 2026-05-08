using ForqStudio.Application.Abstractions.Caching;
using ForqStudio.Application.Roles.AssignPermissions;
using ForqStudio.Application.UnitTests.Permissions;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Roles;

public class AssignPermissionsTests
{
    private readonly AssignPermissionsCommandHandler _handler;
    private readonly IRoleRepository _roleRepositoryMock;
    private readonly IPermissionRepository _permissionRepositoryMock;
    private readonly ICacheService _cacheServiceMock;
    private readonly IUnitOfWork _unitOfWorkMock;

    public AssignPermissionsTests()
    {
        _roleRepositoryMock = Substitute.For<IRoleRepository>();
        _permissionRepositoryMock = Substitute.For<IPermissionRepository>();
        _cacheServiceMock = Substitute.For<ICacheService>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();

        _handler = new AssignPermissionsCommandHandler(
            _roleRepositoryMock,
            _permissionRepositoryMock,
            _cacheServiceMock,
            _unitOfWorkMock);
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenRoleNotFound()
    {
        // Arrange
        var command = new AssignPermissionsCommand(100, new List<int> { 1 });

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
    public async Task Handle_Should_ReturnFailure_WhenPermissionNotFound()
    {
        // Arrange
        var command = new AssignPermissionsCommand(100, new List<int> { 1, 2, 3 });
        var role = RoleData.Create(100);

        _roleRepositoryMock
            .GetByIdAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(role);

        // Only return permission 1, missing 2 and 3
        _permissionRepositoryMock
            .GetByIdsAsync(command.PermissionIds, Arg.Any<CancellationToken>())
            .Returns(new List<Permission> { PermissionData.Create(1) });

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Role.PermissionsNotFound");
        result.Error.name.Should().Contain("2");
        result.Error.name.Should().Contain("3");
    }

    [Fact]
    public async Task Handle_Should_ReturnSuccess_WhenPermissionsAssigned()
    {
        // Arrange
        var command = new AssignPermissionsCommand(100, new List<int> { 1, 2 });
        var role = RoleData.Create(100);

        _roleRepositoryMock
            .GetByIdAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(role);

        _permissionRepositoryMock
            .GetByIdsAsync(command.PermissionIds, Arg.Any<CancellationToken>())
            .Returns(new List<Permission> { PermissionData.Create(1), PermissionData.Create(2) });

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_Should_AddPermissionsToRole_WhenSuccessful()
    {
        // Arrange
        var command = new AssignPermissionsCommand(100, new List<int> { 1 });
        var role = RoleData.Create(100);
        var permission = PermissionData.Create(1);

        _roleRepositoryMock
            .GetByIdAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(role);

        _permissionRepositoryMock
            .GetByIdsAsync(command.PermissionIds, Arg.Any<CancellationToken>())
            .Returns(new List<Permission> { permission });

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        await _handler.Handle(command, default);

        // Assert
        role.Permissions.Should().Contain(permission);
    }

    [Fact]
    public async Task Handle_Should_CallUnitOfWork_WhenPermissionsAssigned()
    {
        // Arrange
        var command = new AssignPermissionsCommand(100, new List<int> { 1 });
        var role = RoleData.Create(100);

        _roleRepositoryMock
            .GetByIdAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(role);

        _permissionRepositoryMock
            .GetByIdsAsync(command.PermissionIds, Arg.Any<CancellationToken>())
            .Returns(new List<Permission> { PermissionData.Create(1) });

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        await _handler.Handle(command, default);

        // Assert
        await _unitOfWorkMock.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_InvalidateCaches_WhenPermissionsAssigned()
    {
        // Arrange
        var command = new AssignPermissionsCommand(100, new List<int> { 1 });
        var role = RoleData.Create(100);
        var identityIds = new List<string> { "identity1", "identity2" };

        _roleRepositoryMock
            .GetByIdAsync(command.RoleId, Arg.Any<CancellationToken>())
            .Returns(role);

        _permissionRepositoryMock
            .GetByIdsAsync(command.PermissionIds, Arg.Any<CancellationToken>())
            .Returns(new List<Permission> { PermissionData.Create(1) });

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
}
