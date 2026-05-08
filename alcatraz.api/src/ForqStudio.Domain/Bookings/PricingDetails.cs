using ForqStudio.Domain.Shared;

namespace ForqStudio.Domain.Bookings
{
    public record PricingDetails(
        Money PriceForDuration,
        Money CleaningFee,
        Money AmenitiesUpCharge,
        Money TotalPrice);
}