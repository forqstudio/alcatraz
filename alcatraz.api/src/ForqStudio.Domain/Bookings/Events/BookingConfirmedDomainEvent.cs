using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Bookings.Events;

public sealed record BookingConfirmedDomainEvent(Guid BookingId) : IDomainEvent
{
}