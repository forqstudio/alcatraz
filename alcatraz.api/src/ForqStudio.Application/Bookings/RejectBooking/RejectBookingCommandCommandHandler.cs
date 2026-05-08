using ForqStudio.Application.Abstractions.Clock;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Bookings;

namespace ForqStudio.Application.Bookings.RejectBooking;

internal sealed class RejectBookingCommandCommandHandler(
    IDateTimeProvider dateTimeProvider,
    IBookingRepository bookingRepository,
    IUnitOfWork unitOfWork

    ) : ICommandHandler<RejectBookingCommand>
{
    public async Task<Result> Handle(
        RejectBookingCommand request,
        CancellationToken cancellationToken)
    {
        var booking = await bookingRepository.GetByIdAsync(request.BookingId, cancellationToken);

        if (booking is null)
        {
            return Result.Failure(BookingErrors.NotFound);
        }

        var result = booking.Reject(dateTimeProvider.UtcNow);

        if (result.IsFailure)
        {
            return result;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}