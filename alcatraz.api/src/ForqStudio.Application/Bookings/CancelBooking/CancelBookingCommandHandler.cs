using ForqStudio.Application.Abstractions.Clock;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Bookings;

namespace ForqStudio.Application.Bookings.CancelBooking;

internal sealed class CancelBookingCommandHandler(
    IDateTimeProvider dateTimeProvider,
    IBookingRepository bookingRepository,
    IUnitOfWork unitOfWork
    ) : ICommandHandler<CancelBookingCommand>
{
    public async Task<Result> Handle(
        CancelBookingCommand request,
        CancellationToken cancellationToken)
    {
        var booking = await bookingRepository.GetByIdAsync(request.BookingId, cancellationToken);

        if (booking is null)
        {
            return Result.Failure(BookingErrors.NotFound);
        }

        var result = booking.Cancel(dateTimeProvider.UtcNow);

        if (result.IsFailure)
        {
            return result;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}