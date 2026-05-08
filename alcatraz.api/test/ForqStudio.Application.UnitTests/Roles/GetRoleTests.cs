using ForqStudio.Application.Roles.GetRole;
using ForqStudio.Application.UnitTests.Permissions;
using ForqStudio.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Roles;

public class GetRoleTests
{
    private readonly GetRoleQueryHandler _handler;
    private readonly IRoleRepository _roleRepositoryMock;

    public GetRoleTests()
    {
        _roleRepositoryMock = Substitute.For<IRoleRepository>();
        _handler = new GetRoleQueryHandler(_roleRepositoryMock);
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenRoleNotFound()
    {
        // Arrange
        var query = new GetRoleQuery(100);

        _roleRepositoryMock
            .GetByIdAsync(query.Id, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(RoleErrors.NotFound);
    }

    [Fact]
    public async Task Handle_Should_ReturnSuccess_WhenRoleExists()
    {
        // Arrange
        var query = new GetRoleQuery(100);
        var role = RoleData.Create(100, "testrole");

        _roleRepositoryMock
            .GetByIdAsync(query.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(100);
        result.Value.Name.Should().Be("testrole");
    }

    [Fact]
    public async Task Handle_Should_ReturnRoleWithPermissions_WhenRoleHasPermissions()
    {
        // Arrange
        var query = new GetRoleQuery(100);
        var permission1 = PermissionData.Create(1, "users.read");
        var permission2 = PermissionData.Create(2, "users.write");
        var role = RoleData.CreateWithPermissions(100, "testrole", permission1, permission2);

        _roleRepositoryMock
            .GetByIdAsync(query.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Permissions.Should().HaveCount(2);
        result.Value.Permissions.Should().Contain(p => p.Id == 1 && p.Name == "users.read");
        result.Value.Permissions.Should().Contain(p => p.Id == 2 && p.Name == "users.write");
    }

    [Fact]
    public async Task Handle_Should_ReturnEmptyPermissions_WhenRoleHasNoPermissions()
    {
        // Arrange
        var query = new GetRoleQuery(100);
        var role = RoleData.Create(100, "testrole");

        _roleRepositoryMock
            .GetByIdAsync(query.Id, Arg.Any<CancellationToken>())
            .Returns(role);

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Permissions.Should().BeEmpty();
    }
}
