using FlowForge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowForge.Infrastructure.Persistence.Platform.Configurations;

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EventType).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Payload).IsRequired();
        builder.Property(x => x.StreamName).IsRequired().HasMaxLength(200);
        builder.Property(x => x.SentAt);

        // Relay worker queries by SentAt IS NULL — index keeps it fast
        builder.HasIndex(x => x.SentAt);
    }
}
