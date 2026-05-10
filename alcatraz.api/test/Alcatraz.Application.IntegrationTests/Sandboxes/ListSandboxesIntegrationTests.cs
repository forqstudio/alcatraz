using FluentAssertions;
using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Data;
using Alcatraz.Application.IntegrationTests.Infrastructure;
using Alcatraz.Application.Sandboxes.ListSandboxes;
using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Users;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Alcatraz.Application.IntegrationTests.Sandboxes;

public class ListSandboxesIntegrationTests : BaseIntegrationTest
{
    private readonly IntegrationTestWebAppFactory _factory;

    public ListSandboxesIntegrationTests(IntegrationTestWebAppFactory factory)
        : base(factory)
    {
        _factory = factory;
    }

    private static User MakeUser(string label) =>
        User.Create(
            new FirstName(label),
            new LastName("user"),
            new Email($"sandbox-list-{label}+{Guid.NewGuid():N}@test.com"));

    [Fact]
    public async Task Handle_ExcludesDeletedSandboxes()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Alcatraz.Infrastructure.ApplicationDbContext>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var sqlFactory = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        var user = MakeUser("excludes-deleted");
        user.SetIdentityId(Guid.NewGuid().ToString());
        userRepo.Add(user);
        await dbContext.SaveChangesAsync();

        var alive1 = Sandbox.Request(user.Id, 2, 2048, DateTime.UtcNow);
        var alive2 = Sandbox.Request(user.Id, 2, 2048, DateTime.UtcNow);
        var deleted = Sandbox.Request(user.Id, 2, 2048, DateTime.UtcNow);
        deleted.MarkDeleting(DateTime.UtcNow);
        deleted.MarkDestroyed(DateTime.UtcNow);
        deleted.Status.Should().Be(SandboxStatus.Deleted);
        dbContext.Set<Sandbox>().AddRange(alive1, alive2, deleted);
        await dbContext.SaveChangesAsync();

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(user.Id);
        var handler = new ListSandboxesQueryHandler(sqlFactory, userContext);

        var result = await handler.Handle(new ListSandboxesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(s => s.Id).Should().BeEquivalentTo(new[] { alive1.Id, alive2.Id });
        result.Value.Select(s => s.Id).Should().NotContain(deleted.Id);
    }

    [Fact]
    public async Task Handle_OrdersByCreatedOnUtc_Descending()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Alcatraz.Infrastructure.ApplicationDbContext>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var sqlFactory = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        var user = MakeUser("orders");
        user.SetIdentityId(Guid.NewGuid().ToString());
        userRepo.Add(user);
        await dbContext.SaveChangesAsync();

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldest = Sandbox.Request(user.Id, 2, 2048, t0);
        var middle = Sandbox.Request(user.Id, 2, 2048, t0.AddHours(1));
        var newest = Sandbox.Request(user.Id, 2, 2048, t0.AddHours(2));
        dbContext.Set<Sandbox>().AddRange(oldest, middle, newest);
        await dbContext.SaveChangesAsync();

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(user.Id);
        var handler = new ListSandboxesQueryHandler(sqlFactory, userContext);

        var result = await handler.Handle(new ListSandboxesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(s => s.Id).Should().Equal(newest.Id, middle.Id, oldest.Id);
    }

    [Fact]
    public async Task Handle_OnlyReturnsCallerOwnedSandboxes()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Alcatraz.Infrastructure.ApplicationDbContext>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var sqlFactory = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        var caller = MakeUser("caller");
        var other = MakeUser("other");
        caller.SetIdentityId(Guid.NewGuid().ToString());
        other.SetIdentityId(Guid.NewGuid().ToString());
        userRepo.Add(caller);
        userRepo.Add(other);
        await dbContext.SaveChangesAsync();

        var mine = Sandbox.Request(caller.Id, 2, 2048, DateTime.UtcNow);
        var theirs = Sandbox.Request(other.Id, 2, 2048, DateTime.UtcNow);
        dbContext.Set<Sandbox>().AddRange(mine, theirs);
        await dbContext.SaveChangesAsync();

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(caller.Id);
        var handler = new ListSandboxesQueryHandler(sqlFactory, userContext);

        var result = await handler.Handle(new ListSandboxesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(s => s.Id).Should().Contain(mine.Id);
        result.Value.Select(s => s.Id).Should().NotContain(theirs.Id);
    }
}
