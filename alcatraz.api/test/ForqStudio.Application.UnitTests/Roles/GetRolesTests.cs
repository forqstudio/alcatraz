using ForqStudio.Application.Roles.GetRoles;
using ForqStudio.Application.UnitTests.Permissions;
using ForqStudio.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Roles;

public class GetRolesTests
{
    private readonly GetRolesQueryHandler _handler;
    private readonly IRoleRepository _roleRepositoryMock;

    public GetRolesTests()
    {
        _roleRepositoryMock = Substitute.For<IRoleRepository>();
        _handler = new GetRolesQueryHandler(_roleRepositoryMock);
    }

    [Fact]
    public async Task Handle_Should_ReturnEmptyList_WhenNoRolesExist()
    {
        // Arrange
        var query = new GetRolesQuery();

        _roleRepositoryMock
            .GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Role>());

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_Should_ReturnAllRoles_WhenRolesExist()
    {
        // Arrange
        var query = new GetRolesQuery();
        var roles = new List<Role>
        {
            RoleData.Create(1, "admin"),
            RoleData.Create(2, "user"),
            RoleData.Create(3, "moderator")
        };

        _roleRepositoryMock
            .GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(roles);

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
        result.Value.Should().Contain(r => r.Name == "admin");
        result.Value.Should().Contain(r => r.Name == "user");
        result.Value.Should().Contain(r => r.Name == "moderator");
    }

    [Fact]
    public async Task Handle_Should_IncludePermissions_WhenRolesHavePermissions()
    {
        // Arrange
        var query = new GetRolesQuery();
        var permission = PermissionData.Create(1, "users.read");
        var role = RoleData.CreateWithPermissions(1, "admin", permission);
        var roles = new List<Role> { role };

        _roleRepositoryMock
            .GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(roles);

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Permissions.Should().HaveCount(1);
        result.Value[0].Permissions[0].Name.Should().Be("users.read");
    }
}
