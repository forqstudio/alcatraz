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

        builder.Property(sandbox => sandbox.ActualVcpus);

        builder.Property(sandbox => sandbox.ActualMemoryMib);

        builder.Property(sandbox => sandbox.BootDurationMs);

        builder.Property(sandbox => sandbox.ReadyAtUtc);

        builder.Property(sandbox => sandbox.VmmVersion).HasMaxLength(64);

        builder.Property(sandbox => sandbox.VmmState).HasMaxLength(32);

        builder.Property(sandbox => sandbox.FirecrackerPid);

        builder.Property(sandbox => sandbox.SocketPath).HasMaxLength(255);

        builder.Property(sandbox => sandbox.TapDevice).HasMaxLength(32);

        builder.Property(sandbox => sandbox.MacAddress).HasMaxLength(17);

        builder.Property(sandbox => sandbox.VmIp).HasMaxLength(45);

        builder.Property(sandbox => sandbox.HostGatewayIp).HasMaxLength(45);

        builder.Property(sandbox => sandbox.NfsPort);

        builder.Property(sandbox => sandbox.WorkerSlotIndex);

        builder.Property(sandbox => sandbox.RootfsPath).HasMaxLength(512);

        builder.Property(sandbox => sandbox.KernelPath).HasMaxLength(512);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(sandbox => sandbox.OwnerUserId);

        builder.HasIndex(sandbox => sandbox.OwnerUserId);

        builder.HasIndex(sandbox => sandbox.Status);
    }
}
