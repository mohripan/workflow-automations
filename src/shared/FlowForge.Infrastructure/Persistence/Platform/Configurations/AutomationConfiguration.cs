using FlowForge.Domain.Entities;
using FlowForge.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Text.Json;

namespace FlowForge.Infrastructure.Persistence.Platform.Configurations;

public class AutomationConfiguration : IEntityTypeConfiguration<Automation>
{
    public void Configure(EntityTypeBuilder<Automation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.IsEnabled).IsRequired();

        builder.HasMany(x => x.Triggers)
            .WithOne()
            .HasForeignKey(x => x.AutomationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.ConditionRoot)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<TriggerConditionNode>(v, (JsonSerializerOptions?)null)!);
    }
}
