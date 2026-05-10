using FluentAssertions;
using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Data;
using Alcatraz.Application.IntegrationTests.Infrastructure;
using Alcatraz.Application.Sandboxes.GetSandbox;
using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Users;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Alcatraz.Application.IntegrationTests.Sandboxes;

public class GetSandboxIntegrationTests : BaseIntegrationTest
{
    private readonly IntegrationTestWebAppFactory _factory;

    public GetSandboxIntegrationTests(IntegrationTestWebAppFactory factory)
        : base(factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Handle_WhenOwnedByAnotherUser_ReturnsNotFound()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Alcatraz.Infrastructure.ApplicationDbContext>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var sqlFactory = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        var owner = User.Create(
            new FirstName("owner"),
            new LastName("user"),
            new Email($"sandbox-get-owner+{Guid.NewGuid():N}@test.com"));
        owner.SetIdentityId(Guid.NewGuid().ToString());
        var intruder = User.Create(
            new FirstName("intruder"),
            new LastName("user"),
            new Email($"sandbox-get-intruder+{Guid.NewGuid():N}@test.com"));
        intruder.SetIdentityId(Guid.NewGuid().ToString());
        userRepo.Add(owner);
        userRepo.Add(intruder);
        await dbContext.SaveChangesAsync();

        var sandbox = Sandbox.Request(owner.Id, vcpus: 2, memoryMib: 2048, DateTime.UtcNow);
        dbContext.Set<Sandbox>().Add(sandbox);
        await dbContext.SaveChangesAsync();

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(intruder.Id);
        var handler = new GetSandboxQueryHandler(sqlFactory, userContext);

        var result = await handler.Handle(new GetSandboxQuery(sandbox.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxErrors.NotFound);
    }
}
