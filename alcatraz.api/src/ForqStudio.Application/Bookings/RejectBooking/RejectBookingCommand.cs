using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Bookings.RejectBooking;

public sealed record RejectBookingCommand(Guid BookingId) : ICommand;