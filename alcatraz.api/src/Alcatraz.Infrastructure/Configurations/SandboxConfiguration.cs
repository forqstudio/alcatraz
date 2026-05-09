using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alcatraz.Infrastructure.Configurations;

internal sealed class SandboxConfiguration : IEntityTypeConfiguration<Sandbox>
{
    public void Configure(EntityTypeBuilder<Sandbox> builder)
    {
        builder.ToTable("sandboxes");

        builder.HasKey(sandbox => sandbox.Id);

        builder.Property(sandbox => sandbox.OwnerUserId).IsRequired();

        builder.Property(sandbox => sandbox.RequestedVcpus).IsRequired();

        builder.Property(sandbox => sandbox.RequestedMemoryMib).IsRequired();

        builder.Property(sandbox => sandbox.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(sandbox => sandbox.CreatedOnUtc).IsRequired();

        builder.Property(sandbox => sandbox.Host).HasMaxLength(255);

        builder.Property(sandbox => sandbox.Port);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(sandbox => sandbox.OwnerUserId);

        builder.HasIndex(sandbox => sandbox.OwnerUserId);

        builder.HasIndex(sandbox => sandbox.Status);
    }
}
