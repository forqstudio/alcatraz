using ForqStudio.Application.Permissions.CreatePermission;
using ForqStudio.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Permissions;

public class CreatePermissionTests
{
    private readonly IPermissionRepository _permissionRepositoryMock;

    public CreatePermissionTests()
    {
        _permissionRepositoryMock = Substitute.For<IPermissionRepository>();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("UPPER.CASE")]
    [InlineData("no-dot")]
    [InlineData("too.many.dots")]
    [InlineData("")]
    public async Task Handle_Should_ReturnFailure_WhenNameIsInvalid(string name)
    {
        // Arrange
        var handler = CreateHandler();
        var command = new CreatePermissionCommand(name);

        // Act
        var result = await handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(PermissionErrors.InvalidName);
    }

    [Fact]
    public async Task Handle_Should_ReturnFailure_WhenPermissionAlreadyExists()
    {
        // Arrange
        var handler = CreateHandler();
        var command = new CreatePermissionCommand("test.permission");
        var existingPermission = PermissionData.Create();

        _permissionRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns(existingPermission);

        // Act
        var result = await handler.Handle(command, default);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(PermissionErrors.AlreadyExists);
    }

    [Fact]
    public async Task Handle_Should_ReturnSuccess_WhenPermissionIsCreated()
    {
        // Arrange
        var handler = CreateHandler();
        var command = new CreatePermissionCommand("test.permission");

        _permissionRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns((Permission?)null);

        // Act
        var result = await handler.Handle(command, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_Should_CallRepositoryAdd_WhenPermissionIsCreated()
    {
        // Arrange
        var handler = CreateHandler();
        var command = new CreatePermissionCommand("test.permission");

        _permissionRepositoryMock
            .GetByNameAsync(command.Name, Arg.Any<CancellationToken>())
            .Returns((Permission?)null);

        // Act
        await handler.Handle(command, default);

        // Assert
        _permissionRepositoryMock.Received(1).Add(Arg.Is<Permission>(p => p.Name == "test.permission"));
    }

    private CreatePermissionCommandHandler CreateHandler()
    {
        var unitOfWorkMock = Substitute.For<ForqStudio.Domain.Abstractions.IUnitOfWork>();

        return new CreatePermissionCommandHandler(
            _permissionRepositoryMock,
            unitOfWorkMock);
    }
}
