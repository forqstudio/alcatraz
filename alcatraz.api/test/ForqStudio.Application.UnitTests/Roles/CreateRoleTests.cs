using ForqStudio.Application.Roles.CreateRole;
using ForqStudio.Application.UnitTests.Permissions;
using ForqStudio.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Roles;

public class CreateRoleTests
{
    private readonly IRoleRepository _roleRepositoryMock;
    private readonly IPermissionRepository _permissionRepositoryMock;

    public CreateRoleTests()
    {
        _roleRepositoryMock = Substitute.For<IRoleRepository>();
        _permissionRepositoryMock = Substitute.For<IPermissionRepository>();
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenRoleAlreadyExists()
    {
        // Arrange
        var handler = CreateHandler();
        var command = new CreateRoleCommand("existingrole", new List<int> { 1 });
        var existingRole = RoleData.Create(100, "existingrole");

        _roleRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns(existingRole);

        // Act
        var result = await handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(RoleErrors.AlreadyExists);
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenPermissionNotFound()
    {
        // Arrange
        var handler = CreateHandler();
        var command = new CreateRoleCommand("newrole", new List<int> { 100, 200, 300 });

        _roleRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

        // Only return permission 100, missing 200 and 300
        _permissionRepositoryMock
            .GetByIdsAsync(command.PermissionIds, Arg.Any<CancellationToken>())
            .Returns(new List<Permission> { PermissionData.Create(100) });

        // Act
        var result = await handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Role.PermissionsNotFound");
        result.Error.name.Should().Contain("200");
        result.Error.name.Should().Contain("300");
    }

    [Fact]
    public async Task Handle_Should_ReturnSuccess_WhenRoleIsCreated()
    {
        // Arrange
        var handler = CreateHandler();
        var command = new CreateRoleCommand("newrole", new List<int> { 1 });

        _roleRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

        _permissionRepositoryMock
            .GetByIdsAsync(command.PermissionIds, Arg.Any<CancellationToken>())
            .Returns(new List<Permission> { PermissionData.Create(1) });

        // Act
        var result = await handler.Handle(command, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    private CreateRoleCommandHandler CreateHandler()
    {
        var unitOfWorkMock = Substitute.For<ForqStudio.Domain.Abstractions.IUnitOfWork>();

        return new CreateRoleCommandHandler(
            _roleRepositoryMock,
            _permissionRepositoryMock,
            unitOfWorkMock);
    }
}
