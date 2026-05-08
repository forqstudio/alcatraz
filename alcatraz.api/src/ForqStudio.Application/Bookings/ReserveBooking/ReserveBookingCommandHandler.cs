using ForqStudio.Application.Abstractions.Clock;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Application.Exceptions;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Apartments;
using ForqStudio.Domain.Bookings;
using ForqStudio.Domain.Users;

namespace ForqStudio.Application.Bookings.ReserveBooking;

internal sealed class ReserveBookingCommandHandler(
    IUserRepository userRepository,
    IBookingRepository bookingRepository,
    IApartmentRepository apartmentRepository,
    PricingService pricingService,
    IUnitOfWork unitOfWork,
    IDateTimeProvider dateTimeProvider
    ) : ICommandHandler<ReserveBookingCommand, Guid>
{
    public async Task<Result<Guid>> Handle(ReserveBookingCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<Guid>(UserErrors.NotFound);

        var apartment = await apartmentRepository.GetByIdAsync(request.ApartmentId, cancellationToken);
        if (apartment is null)
            return Result.Failure<Guid>(ApartmentErrors.NotFound);

        var duration = DateRange.Create(request.StartDate, request.EndDate);

        if (await bookingRepository.IsOverlappingAsync(apartment, duration, cancellationToken))
            return Result.Failure<Guid>(BookingErrors.Overlap);

        try
        {
            var booking = Booking.Reserve(
            apartment,
            user.Id,
            duration,
            dateTimeProvider.UtcNow,
            pricingService);

            bookingRepository.Add(booking);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            return booking.Id;
        }
        catch (ConcurrencyException)
        {
            return Result.Failure<Guid>(BookingErrors.Overlap);
        }
    }
}
