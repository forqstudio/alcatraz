using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Users.Events;

public sealed record UserCreatedDomainEvent(Guid UserId) : IDomainEvent;

