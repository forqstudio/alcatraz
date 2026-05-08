using ForqStudio.Application.Permissions.GetPermission;
using ForqStudio.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Permissions;

public class GetPermissionTests
{
    private readonly GetPermissionQueryHandler _handler;
    private readonly IPermissionRepository _permissionRepositoryMock;

    public GetPermissionTests()
    {
        _permissionRepositoryMock = Substitute.For<IPermissionRepository>();

        _handler = new GetPermissionQueryHandler(_permissionRepositoryMock);
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenPermissionNotFound()
    {
        // Arrange
        var query = new GetPermissionQuery(100);

        _permissionRepositoryMock
            .GetByIdAsync(query.Id, Arg.Any<CancellationToken>())
            .Returns((Permission?)null);

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(PermissionErrors.NotFound);
    }

    [Fact]
    public async Task Handle_Should_ReturnSuccess_WhenPermissionFound()
    {
        // Arrange
        var query = new GetPermissionQuery(100);
        var permission = PermissionData.Create(100, "test.permission");

        _permissionRepositoryMock
            .GetByIdAsync(query.Id, Arg.Any<CancellationToken>())
            .Returns(permission);

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_Should_ReturnCorrectResponse_WhenPermissionFound()
    {
        // Arrange
        var query = new GetPermissionQuery(100);
        var permission = PermissionData.Create(100, "test.permission");

        _permissionRepositoryMock
            .GetByIdAsync(query.Id, Arg.Any<CancellationToken>())
            .Returns(permission);

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.Value.Id.Should().Be(100);
        result.Value.Name.Should().Be("test.permission");
    }
}
