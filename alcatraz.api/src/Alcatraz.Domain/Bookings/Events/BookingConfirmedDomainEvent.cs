using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Domain.Bookings.Events;

public sealed record BookingConfirmedDomainEvent(Guid BookingId) : IDomainEvent
{
}