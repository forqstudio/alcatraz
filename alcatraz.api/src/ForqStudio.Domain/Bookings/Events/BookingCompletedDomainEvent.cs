using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Bookings.Events;

public sealed record BookingCompletedDomainEvent(Guid BookingId) : IDomainEvent
{
}