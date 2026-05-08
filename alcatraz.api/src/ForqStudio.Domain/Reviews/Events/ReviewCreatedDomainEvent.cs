using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Reviews.Events;

public sealed record ReviewCreatedDomainEvent(Guid ReviewId) : IDomainEvent;