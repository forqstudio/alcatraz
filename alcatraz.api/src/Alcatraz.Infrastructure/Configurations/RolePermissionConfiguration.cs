using Alcatraz.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alcatraz.Infrastructure.Configurations;

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions");

        builder.HasKey(rolePermission => new { rolePermission.RoleId, rolePermission.PermissionId });

        builder.HasData(
            new RolePermission(Role.User.Id, Permission.UsersRead.Id),
            new RolePermission(Role.User.Id, Permission.SandboxesRead.Id),
            new RolePermission(Role.User.Id, Permission.SandboxesWrite.Id),
            new RolePermission(Role.User.Id, Permission.SandboxesSsh.Id));
    }
}