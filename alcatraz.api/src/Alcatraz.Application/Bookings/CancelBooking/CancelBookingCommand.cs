using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Bookings.CancelBooking;

public record CancelBookingCommand(Guid BookingId) : ICommand;