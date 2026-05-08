using ForqStudio.Application.Abstractions.Email;
using ForqStudio.Domain.Bookings;
using ForqStudio.Domain.Bookings.Events;
using ForqStudio.Domain.Users;
using MediatR;

namespace ForqStudio.Application.Bookings.ReserveBooking;

internal sealed record BookingReservedDomainHandler(
    IBookingRepository bookingRepository,
    IUserRepository userRepository,
    IEmailService emailService

    ) : INotificationHandler<BookingReservedDomainEvent>
{
    public async Task Handle(BookingReservedDomainEvent notification, CancellationToken cancellationToken)
    {
        var booking = await bookingRepository.GetByIdAsync(notification.BookingId, cancellationToken);
        if (booking is null)
            return;

        var user = await userRepository.GetByIdAsync(booking.UserId, cancellationToken);
        if (user is null)
            return;

        await emailService.SendEmailAsync(
            user.Email,
            "Booking Reserved",
            "Your booking has been reserved. You have 10 minutes to confirm your booking");
    }
}
