using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Bookings.CompleteBooking;

public record CompleteBookingCommand(Guid BookingId) : ICommand;