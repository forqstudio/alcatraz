using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Domain.Bookings.Events;

public sealed record BookingReservedDomainEvent(Guid BookingId) : IDomainEvent
{
}