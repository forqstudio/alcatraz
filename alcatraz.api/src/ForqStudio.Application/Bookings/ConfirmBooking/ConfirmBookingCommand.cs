using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Bookings.ConfirmBooking;

public sealed record ConfirmBookingCommand(Guid BookingId) : ICommand;