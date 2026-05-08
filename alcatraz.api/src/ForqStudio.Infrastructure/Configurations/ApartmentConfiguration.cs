using ForqStudio.Domain.Apartments;
using ForqStudio.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ForqStudio.Infrastructure.Configurations;

internal sealed class ApartmentConfiguration : IEntityTypeConfiguration<Apartment>
{
    public void Configure(EntityTypeBuilder<Apartment> builder)
    {
        builder.ToTable("apartments");
        builder.HasKey(apartment => apartment.Id);
        
        builder.OwnsOne(apartment => apartment.Address);

        builder.OwnsOne(apartment => apartment.Price, priceBuilder =>
        {
            priceBuilder.Property(money => money.Currency)
            .HasConversion(currency => currency.Code, code => Currency.FromCode(code));
        });

        builder.OwnsOne(apartment => apartment.CleaningFee, priceBuilder =>
        {
            priceBuilder.Property(money => money.Currency)
            .HasConversion(currency => currency.Code, code => Currency.FromCode(code));
        });

        builder.Property(apartment => apartment.Name)
            .HasMaxLength(200)
            .HasConversion(name => name.Value, value => new Name(value));

        builder.Property(apartment => apartment.Description)
           .HasMaxLength(2000)
           .HasConversion(description => description.Value, value => new Description(value));

        builder.Property<uint>("version").IsRowVersion();
    }
}
