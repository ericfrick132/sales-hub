using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class InstagramMetricsConfiguration : IEntityTypeConfiguration<InstagramMetrics>
{
    public void Configure(EntityTypeBuilder<InstagramMetrics> b)
    {
        b.ToTable("instagram_metrics");
        b.HasKey(x => x.Id);

        b.HasOne(x => x.InstagramAccount)
            .WithMany()
            .HasForeignKey(x => x.InstagramAccountId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.InstagramAccountId, x.Date }).IsUnique();
    }
}
