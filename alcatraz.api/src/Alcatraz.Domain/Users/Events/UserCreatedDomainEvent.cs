using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Domain.Users.Events;

public sealed record UserCreatedDomainEvent(Guid UserId) : IDomainEvent;

