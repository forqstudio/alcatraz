using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Sandboxes.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alcatraz.Infrastructure.Configurations;

internal sealed class SandboxUsageRecordConfiguration : IEntityTypeConfiguration<SandboxUsageRecord>
{
    public void Configure(EntityTypeBuilder<SandboxUsageRecord> builder)
    {
        builder.ToTable("sandbox_usage_records");

        builder.HasKey(record => record.Id);

        builder.Property(record => record.BillingWindowStartUtc).IsRequired();

        builder.Property(record => record.BillingWindowEndUtc).IsRequired();

        builder.Property(record => record.ProvisionedVcpuSeconds).IsRequired();

        builder.Property(record => record.ProvisionedMemoryMibSeconds).IsRequired();

        builder.Property(record => record.ActualCpuUsageUsec);

        builder.Property(record => record.ActualNetRxBytes);

        builder.Property(record => record.ActualNetTxBytes);

        builder.Property(record => record.SampleCount).IsRequired();

        builder.Property(record => record.FinalisedAtUtc).IsRequired();

        builder.Ignore(record => record.SandboxId);

        builder.HasOne<Sandbox>()
            .WithOne()
            .HasForeignKey<SandboxUsageRecord>(record => record.Id);
    }
}
