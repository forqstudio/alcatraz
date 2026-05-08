using ForqStudio.Domain.Sandboxes;
using ForqStudio.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ForqStudio.Infrastructure.Configurations;

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

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(sandbox => sandbox.OwnerUserId);

        builder.HasIndex(sandbox => sandbox.OwnerUserId);

        builder.HasIndex(sandbox => sandbox.Status);
    }
}
