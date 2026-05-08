using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Bookings.CancelBooking;

public record CancelBookingCommand(Guid BookingId) : ICommand;