using FlowForge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowForge.Infrastructure.Persistence.Platform.Configurations;

public class HostGroupConfiguration : IEntityTypeConfiguration<HostGroup>
{
    public void Configure(EntityTypeBuilder<HostGroup> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.ConnectionId).IsRequired().HasMaxLength(50);
        builder.HasIndex(x => x.ConnectionId).IsUnique();

        builder.HasMany(x => x.RegistrationTokens)
            .WithOne()
            .HasForeignKey(x => x.HostGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.RegistrationTokens)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
