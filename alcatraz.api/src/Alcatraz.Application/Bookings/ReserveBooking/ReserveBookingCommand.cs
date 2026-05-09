using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Bookings.ReserveBooking;

public sealed record ReserveBookingCommand(
    Guid ApartmentId,
    Guid UserId,
    DateOnly StartDate,
    DateOnly EndDate) : ICommand<Guid>;
