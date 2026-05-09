using Alcatraz.Application.Abstractions.Caching;

namespace Alcatraz.Application.Bookings.GetBooking;

public sealed record GetBookingQuery(Guid BookingId) : ICachedQuery<BookingResponse>
{
    public string CacheKey => CacheKeys.Booking(BookingId);

    public TimeSpan? Expiration => null;
}