using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Domain.Bookings.Events;

public sealed record BookingRejectedDomainEvent(Guid BookingId) : IDomainEvent
{
}