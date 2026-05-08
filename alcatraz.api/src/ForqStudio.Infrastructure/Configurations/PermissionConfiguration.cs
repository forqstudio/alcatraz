using ForqStudio.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ForqStudio.Infrastructure.Configurations
{
    internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
    {
        public void Configure(EntityTypeBuilder<Permission> builder)
        {
            builder.ToTable("Permissions");

            builder.HasKey(p => p.Id);

            builder.HasQueryFilter(p => !p.IsDeleted);

            builder.HasData(Permission.UsersRead, Permission.UsersWrite);
        }
    }
}
