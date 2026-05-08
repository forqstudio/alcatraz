using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Bookings.Events;

public sealed record BookingRejectedDomainEvent(Guid BookingId) : IDomainEvent
{
}