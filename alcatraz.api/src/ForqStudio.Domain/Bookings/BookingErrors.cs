using ForqStudio.Domain.Abstractions;


namespace ForqStudio.Domain.Bookings;

public static class BookingErrors
{
    public static Error NotFound => new(
        "Booking.NotFound",
        "Booking with the specified identifier was not found");

    public static Error Overlap = new(
        "Booking.Overlap",
        "Booking overlaps with another booking");

    public static Error NotReserved = new(
        "Booking.NotReserved",
        "Booking is not pending");

    public static Error NotConfirmed = new(
        "Booking.NotConfirmed",
        "Booking is not confirmed");

    public static Error AlreadyStarted = new(
        "Booking.AlreadyStarted",
        "Booking has already started");
}
