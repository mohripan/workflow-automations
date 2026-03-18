using FlowForge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowForge.Infrastructure.Persistence.Platform.Configurations;

public class TriggerConfiguration : IEntityTypeConfiguration<Trigger>
{
    public void Configure(EntityTypeBuilder<Trigger> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.TypeId).IsRequired().HasColumnType("varchar(100)");
        builder.Property(x => x.ConfigJson).IsRequired().HasColumnType("jsonb");

        builder.HasIndex(x => new { x.AutomationId, x.Name }).IsUnique();
    }
}
