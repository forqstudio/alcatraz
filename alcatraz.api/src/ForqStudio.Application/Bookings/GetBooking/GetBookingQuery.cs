using ForqStudio.Application.Abstractions.Caching;

namespace ForqStudio.Application.Bookings.GetBooking;

public sealed record GetBookingQuery(Guid BookingId) : ICachedQuery<BookingResponse>
{
    public string CacheKey => CacheKeys.Booking(BookingId);

    public TimeSpan? Expiration => null;
}