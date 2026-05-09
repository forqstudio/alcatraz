using Alcatraz.Domain.Shared;

namespace Alcatraz.Domain.Bookings
{
    public record PricingDetails(
        Money PriceForDuration,
        Money CleaningFee,
        Money AmenitiesUpCharge,
        Money TotalPrice);
}