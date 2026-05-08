namespace ForqStudio.Api.Controllers.Bookings;

public sealed record class ReserveBookingRequest(Guid ApartmentId, Guid UserId, DateOnly StartDate, DateOnly EndDate);

