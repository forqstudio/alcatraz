using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Bookings.Events;

public sealed record BookingCancelledDomainEvent(Guid BookingId) : IDomainEvent
{
}