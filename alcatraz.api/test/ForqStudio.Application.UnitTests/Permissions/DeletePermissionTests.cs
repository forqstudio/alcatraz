using ForqStudio.Application.Permissions.DeletePermission;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Permissions;

public class DeletePermissionTests
{
    private readonly DeletePermissionCommandHandler _handler;
    private readonly IPermissionRepository _permissionRepositoryMock;
    private readonly IUnitOfWork _unitOfWorkMock;

    public DeletePermissionTests()
    {
        _permissionRepositoryMock = Substitute.For<IPermissionRepository>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();

        _handler = new DeletePermissionCommandHandler(
            _permissionRepositoryMock,
            _unitOfWorkMock);
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenPermissionNotFound()
    {
        // Arrange
        var command = new DeletePermissionCommand(100);

        _permissionRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns((Permission?)null);

        // Act
        var result = await _handler.Handle(command, default);

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
        var command = new DeletePermissionCommand(systemPermissionId);
        var systemPermission = PermissionData.CreateSystemPermission(systemPermissionId);

        _permissionRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(systemPermission);

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(PermissionErrors.SystemPermission);
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenPermissionIsInUse()
    {
        // Arrange
        var command = new DeletePermissionCommand(100);
        var permission = PermissionData.Create(100);

        _permissionRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(permission);

        _permissionRepositoryMock
            .IsInUseAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(PermissionErrors.InUse);
    }

    [Fact]
    public async Task Handle_Should_ReturnSuccess_WhenPermissionIsDeleted()
    {
        // Arrange
        var command = new DeletePermissionCommand(100);
        var permission = PermissionData.Create(100);

        _permissionRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(permission);

        _permissionRepositoryMock
            .IsInUseAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_Should_MarkPermissionAsDeleted_WhenSuccessful()
    {
        // Arrange
        var command = new DeletePermissionCommand(100);
        var permission = PermissionData.Create(100);

        _permissionRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(permission);

        _permissionRepositoryMock
            .IsInUseAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        await _handler.Handle(command, default);

        // Assert
        permission.IsDeleted.Should().BeTrue();
        _permissionRepositoryMock.Received(1).Update(permission);
    }

    [Fact]
    public async Task Handle_Should_CallUnitOfWork_WhenPermissionIsDeleted()
    {
        // Arrange
        var command = new DeletePermissionCommand(100);
        var permission = PermissionData.Create(100);

        _permissionRepositoryMock
            .GetByIdAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(permission);

        _permissionRepositoryMock
            .IsInUseAsync(command.Id, Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        await _handler.Handle(command, default);

        // Assert
        await _unitOfWorkMock.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
