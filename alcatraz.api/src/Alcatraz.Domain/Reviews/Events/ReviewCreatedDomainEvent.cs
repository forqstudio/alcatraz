using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Domain.Reviews.Events;

public sealed record ReviewCreatedDomainEvent(Guid ReviewId) : IDomainEvent;