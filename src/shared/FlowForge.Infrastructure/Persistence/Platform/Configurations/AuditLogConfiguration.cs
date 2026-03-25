using FlowForge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowForge.Infrastructure.Persistence.Platform.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Action).IsRequired().HasMaxLength(100);
        builder.Property(x => x.EntityId).HasMaxLength(50);
        builder.Property(x => x.UserId).HasMaxLength(200);
        builder.Property(x => x.Username).HasMaxLength(200);
        builder.Property(x => x.Detail).HasColumnType("text");
        builder.Property(x => x.OccurredAt).IsRequired();

        builder.HasIndex(x => x.EntityId);
        builder.HasIndex(x => x.OccurredAt);
    }
}
