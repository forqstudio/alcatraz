using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Bookings.RejectBooking;

public sealed record RejectBookingCommand(Guid BookingId) : ICommand;