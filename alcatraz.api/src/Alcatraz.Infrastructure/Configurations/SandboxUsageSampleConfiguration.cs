using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Sandboxes.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alcatraz.Infrastructure.Configurations;

internal sealed class SandboxUsageSampleConfiguration : IEntityTypeConfiguration<SandboxUsageSample>
{
    public void Configure(EntityTypeBuilder<SandboxUsageSample> builder)
    {
        builder.ToTable("sandbox_usage_samples");

        builder.HasKey(sample => sample.Id);

        builder.Property(sample => sample.SandboxId).IsRequired();

        builder.Property(sample => sample.SampledAtUtc).IsRequired();

        builder.Property(sample => sample.CpuUsageUsecCumulative);

        builder.Property(sample => sample.NetRxBytesCumulative);

        builder.Property(sample => sample.NetTxBytesCumulative);

        builder.HasOne<Sandbox>()
            .WithMany()
            .HasForeignKey(sample => sample.SandboxId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(sample => new { sample.SandboxId, sample.SampledAtUtc })
            .IsUnique();
    }
}
