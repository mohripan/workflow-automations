using FlowForge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowForge.Infrastructure.Persistence.Platform.Configurations;

public class RegistrationTokenConfiguration : IEntityTypeConfiguration<RegistrationToken>
{
    public void Configure(EntityTypeBuilder<RegistrationToken> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Label).HasMaxLength(100);
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.HasIndex(x => x.HostGroupId);
    }
}
