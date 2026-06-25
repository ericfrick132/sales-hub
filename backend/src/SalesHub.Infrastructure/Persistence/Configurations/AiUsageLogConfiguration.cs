using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class AiUsageLogConfiguration : IEntityTypeConfiguration<AiUsageLog>
{
    public void Configure(EntityTypeBuilder<AiUsageLog> b)
    {
        b.ToTable("ai_usage_logs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Feature).HasMaxLength(40);
        b.Property(x => x.Model).HasMaxLength(64);
        b.Property(x => x.CostUsd).HasColumnType("numeric(14,8)");
        b.HasIndex(x => x.CreatedAt);
        b.HasIndex(x => new { x.CreatedAt, x.Feature });
    }
}
