using ForqStudio.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ForqStudio.Infrastructure.Configurations;

internal sealed class UserSettingsConfiguration : IEntityTypeConfiguration<UserSettings>
{
    public void Configure(EntityTypeBuilder<UserSettings> builder)
    {
        builder.ToTable("user_settings");

        builder.HasKey(us => us.Id);

        builder.Property(us => us.UserId);

        builder.HasIndex(us => us.UserId).IsUnique();

        builder.Property(us => us.PreferredLanguage)
            .HasMaxLength(10);

        builder.Property(us => us.EmailNotificationsEnabled);

        builder.Property(us => us.Timezone)
            .HasMaxLength(100);

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<UserSettings>(us => us.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
