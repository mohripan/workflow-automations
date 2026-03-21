using FlowForge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowForge.Infrastructure.Persistence.Jobs.Configurations;

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.Message).HasMaxLength(1000);
        builder.Property(x => x.TimeoutSeconds);
        builder.Property(x => x.RetryAttempt).HasDefaultValue(0);
        builder.Property(x => x.MaxRetries).HasDefaultValue(0);
        builder.Property(x => x.TaskConfig).HasColumnType("jsonb");
        builder.Property(x => x.OutputJson).HasColumnType("jsonb");
    }
}
