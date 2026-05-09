using FluentAssertions;
using Alcatraz.Application.IntegrationTests.Infrastructure;
using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Sandboxes.Events;
using Alcatraz.Domain.Users;
using Alcatraz.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alcatraz.Application.IntegrationTests.Sandboxes;

public class CreateSandboxIntegrationTests : BaseIntegrationTest
{
    private readonly IntegrationTestWebAppFactory _factory;

    public CreateSandboxIntegrationTests(IntegrationTestWebAppFactory factory)
        : base(factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SaveChanges_PersistsSandbox_AndWritesOutboxMessage()
    {
        // UserRepository.Add attaches Role.User as Unchanged so the seeded role row
        // isn't double-inserted; we route through it instead of DbSet<User>.Add.
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Alcatraz.Infrastructure.ApplicationDbContext>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        var user = User.Create(
            new FirstName("test"),
            new LastName("user"),
            new Email($"sandbox-it+{Guid.NewGuid():N}@test.com"));
        user.SetIdentityId(Guid.NewGuid().ToString());

        userRepo.Add(user);
        await dbContext.SaveChangesAsync();

        var sandbox = Sandbox.Request(user.Id, vcpus: 2, memoryMib: 2048, DateTime.UtcNow);
        dbContext.Set<Sandbox>().Add(sandbox);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Set<Sandbox>().FirstOrDefaultAsync(s => s.Id == sandbox.Id);
        stored.Should().NotBeNull();
        stored!.OwnerUserId.Should().Be(user.Id);
        stored.Status.Should().Be(SandboxStatus.Provisioning);

        var outboxMessages = await dbContext.Set<OutboxMessage>()
            .Where(m => m.Type == nameof(SandboxRequestedDomainEvent))
            .ToListAsync();

        outboxMessages.Should().Contain(m => m.Content.Contains(sandbox.Id.ToString()));
    }
}
