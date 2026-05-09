using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Bookings.CompleteBooking;

public record CompleteBookingCommand(Guid BookingId) : ICommand;