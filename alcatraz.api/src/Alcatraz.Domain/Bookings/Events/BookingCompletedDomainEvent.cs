using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Domain.Bookings.Events;

public sealed record BookingCompletedDomainEvent(Guid BookingId) : IDomainEvent
{
}