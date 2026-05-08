using ForqStudio.Application.Abstractions.Clock;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Bookings;

namespace ForqStudio.Application.Bookings.ConfirmBooking;

internal sealed class ConfirmBookingCommandHandler(
    IDateTimeProvider dateTimeProvider,
    IBookingRepository bookingRepository,
    IUnitOfWork unitOfWork) : ICommandHandler<ConfirmBookingCommand>
{
    public async Task<Result> Handle(
        ConfirmBookingCommand request,
        CancellationToken cancellationToken)
    {
        var booking = await bookingRepository.GetByIdAsync(request.BookingId, cancellationToken);

        if (booking is null)
        {
            return Result.Failure(BookingErrors.NotFound);
        }

        var result = booking.Confirm(dateTimeProvider.UtcNow);

        if (result.IsFailure)
        {
            return result;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}