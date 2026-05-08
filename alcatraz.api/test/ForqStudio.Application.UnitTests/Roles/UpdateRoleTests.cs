using ForqStudio.Application.Abstractions.Caching;
using ForqStudio.Application.Roles.UpdateRole;
using ForqStudio.Application.UnitTests.Permissions;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Roles;

public class UpdateRoleTests
{
    private readonly UpdateRoleCommandHandler _handler;
    private readonly IRoleRepository _roleRepositoryMock;
    private readonly IPermissionRepository _permissionRepositoryMock;
    private readonly ICacheService _cacheServiceMock;
    private readonly IUnitOfWork _unitOfWorkMock;

    public UpdateRoleTests()
    {
        _roleRepositoryMock = Substitute.For<IRoleRepository>();
        _permissionRepositoryMock = Substitute.For<IPermissionRepository>();
        _cacheServiceMock = Substitute.For<ICacheService>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();

        _handler = new UpdateRoleCommandHandler(
            _roleRepositoryMock,
            _permissionRepositoryMock,
            _cacheServiceMock,
            _unitOfWorkMock);
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenRoleNotFound()
    {
        // Arrange
        var command = new UpdateRoleCommand(100, "newname", new List<int> { 1 });

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
        var command = new UpdateRoleCommand(systemRoleId, "newname", new List<int> { 1 });
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
    public async Task Handle_Should_ReturnFailure_WhenNameAlreadyExistsForDifferentRole()
    {
        // Arrange
        var command = new UpdateRoleCommand(100, "existingname", new List<int> { 1 });
        var role = RoleData.Create(100, "originalname");
        var existingRole = RoleData.Create(200, "existingname");

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns(existingRole);

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(RoleErrors.AlreadyExists);
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenPermissionNotFound()
    {
        // Arrange
        var command = new UpdateRoleCommand(100, "newname", new List<int> { 1, 2, 3 });
        var role = RoleData.Create(100, "originalname");

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

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
    public async Task Handle_Should_ReturnSuccess_WhenRoleIsUpdated()
    {
        // Arrange
        var command = new UpdateRoleCommand(100, "newname", new List<int> { 1 });
        var role = RoleData.Create(100, "originalname");

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

        _permissionRepositoryMock
            .GetByIdsAsync(command.PermissionIds, Arg.Any<CancellationToken>())
            .Returns(new List<Permission> { PermissionData.Create(1) });

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_Should_AllowSameName_WhenUpdatingSameRole()
    {
        // Arrange
        var command = new UpdateRoleCommand(100, "samename", new List<int> { 1 });
        var role = RoleData.Create(100, "samename");

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns(role);

        _permissionRepositoryMock
            .GetByIdsAsync(command.PermissionIds, Arg.Any<CancellationToken>())
            .Returns(new List<Permission> { PermissionData.Create(1) });

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_Should_UpdateRoleName_WhenSuccessful()
    {
        // Arrange
        var command = new UpdateRoleCommand(100, "newname", new List<int> { 1 });
        var role = RoleData.Create(100, "originalname");

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

        _permissionRepositoryMock
            .GetByIdsAsync(command.PermissionIds, Arg.Any<CancellationToken>())
            .Returns(new List<Permission> { PermissionData.Create(1) });

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        await _handler.Handle(command, default);

        // Assert
        role.Name.Should().Be("newname");
    }

    [Fact]
    public async Task Handle_Should_AssignPermissions_WhenSuccessful()
    {
        // Arrange
        var command = new UpdateRoleCommand(100, "newname", new List<int> { 1, 2 });
        var role = RoleData.Create(100, "originalname");
        var permission1 = PermissionData.Create(1, "users.read");
        var permission2 = PermissionData.Create(2, "users.write");

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

        _permissionRepositoryMock
            .GetByIdsAsync(command.PermissionIds, Arg.Any<CancellationToken>())
            .Returns(new List<Permission> { permission1, permission2 });

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Act
        await _handler.Handle(command, default);

        // Assert
        role.Permissions.Should().HaveCount(2);
        role.Permissions.Should().Contain(permission1);
        role.Permissions.Should().Contain(permission2);
    }

    [Fact]
    public async Task Handle_Should_CallUnitOfWork_WhenSuccessful()
    {
        // Arrange
        var command = new UpdateRoleCommand(100, "newname", new List<int> { 1 });
        var role = RoleData.Create(100, "originalname");

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

        _permissionRepositoryMock
            .GetByIdsAsync(command.PermissionIds, Arg.Any<CancellationToken>())
            .Returns(new List<Permission> { PermissionData.Create(1) });

        _roleRepositoryMock
            .GetUserIdentityIdsForRoleAsync(command.Id, Arg.Any<CancellationToken>())
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
        var command = new UpdateRoleCommand(100, "newname", new List<int> { 1 });
        var role = RoleData.Create(100, "originalname");
        var identityIds = new List<string> { "identity1", "identity2" };

        _roleRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        _roleRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

        _permissionRepositoryMock
            .GetByIdsAsync(command.PermissionIds, Arg.Any<CancellationToken>())
            .Returns(new List<Permission> { PermissionData.Create(1) });

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
