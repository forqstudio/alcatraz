using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Bookings.ReserveBooking;

public sealed record ReserveBookingCommand(
    Guid ApartmentId,
    Guid UserId,
    DateOnly StartDate,
    DateOnly EndDate) : ICommand<Guid>;
