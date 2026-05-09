using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Bookings.ConfirmBooking;

public sealed record ConfirmBookingCommand(Guid BookingId) : ICommand;