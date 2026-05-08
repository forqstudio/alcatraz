using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Bookings.Events;

public sealed record BookingReservedDomainEvent(Guid BookingId) : IDomainEvent
{
}