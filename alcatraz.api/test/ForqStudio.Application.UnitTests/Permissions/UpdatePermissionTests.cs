using ForqStudio.Application.Abstractions.Caching;
using ForqStudio.Application.Abstractions.Data;
using ForqStudio.Application.Permissions.UpdatePermission;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Permissions;

public class UpdatePermissionTests
{
    private readonly IPermissionRepository _permissionRepositoryMock;
    private readonly ICacheService _cacheServiceMock;
    private readonly ISqlConnectionFactory _sqlConnectionFactoryMock;
    private readonly IUnitOfWork _unitOfWorkMock;

    public UpdatePermissionTests()
    {
        _permissionRepositoryMock = Substitute.For<IPermissionRepository>();
        _cacheServiceMock = Substitute.For<ICacheService>();
        _sqlConnectionFactoryMock = Substitute.For<ISqlConnectionFactory>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenPermissionNotFound()
    {
        // Arrange
        var handler = CreateHandler();
        var command = new UpdatePermissionCommand(100, "new.name");

        _permissionRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns((Permission?)null);

        // Act
        var result = await handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(PermissionErrors.NotFound);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task Handle_Should_ReturnFailure_WhenPermissionIsSystemPermission(int systemPermissionId)
    {
        // Arrange
        var handler = CreateHandler();
        var command = new UpdatePermissionCommand(systemPermissionId, "new.name");
        var systemPermission = PermissionData.CreateSystemPermission(systemPermissionId);

        _permissionRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(systemPermission);

        // Act
        var result = await handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(PermissionErrors.SystemPermission);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("UPPER.CASE")]
    [InlineData("")]
    public async Task Handle_Should_ReturnFailure_WhenNameIsInvalid(string name)
    {
        // Arrange
        var handler = CreateHandler();
        var command = new UpdatePermissionCommand(100, name);
        var permission = PermissionData.Create(100);

        _permissionRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(permission);

        // Act
        var result = await handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(PermissionErrors.InvalidName);
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenNameAlreadyExistsForDifferentPermission()
    {
        // Arrange
        var handler = CreateHandler();
        var command = new UpdatePermissionCommand(100, "existing.name");
        var permission = PermissionData.Create(100);
        var existingPermission = PermissionData.Create(200, "existing.name");

        _permissionRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(permission);

        _permissionRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns(existingPermission);

        // Act
        var result = await handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(PermissionErrors.AlreadyExists);
    }

    private UpdatePermissionCommandHandler CreateHandler()
    {
        return new UpdatePermissionCommandHandler(
            _permissionRepositoryMock,
            _cacheServiceMock,
            _sqlConnectionFactoryMock,
            _unitOfWorkMock);
    }
}
