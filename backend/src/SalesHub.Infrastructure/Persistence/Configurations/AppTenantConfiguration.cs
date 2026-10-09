using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class AppTenantConfiguration : IEntityTypeConfiguration<AppTenant>
{
    public void Configure(EntityTypeBuilder<AppTenant> b)
    {
        b.ToTable("app_tenants");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.ExternalId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Name).HasMaxLength(256);
        b.Property(x => x.Status).HasMaxLength(32).IsRequired();
        b.Property(x => x.Plan).HasMaxLength(128);
        b.Property(x => x.MonthlyAmount).HasPrecision(14, 2);
        b.Property(x => x.Currency).HasMaxLength(8).IsRequired();
        b.HasIndex(x => new { x.ProductKey, x.ExternalId }).IsUnique();
    }
}

public class AppMetricsDailyConfiguration : IEntityTypeConfiguration<AppMetricsDaily>
{
    public void Configure(EntityTypeBuilder<AppMetricsDaily> b)
    {
        b.ToTable("app_metrics_daily");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.MrrUsd).HasPrecision(14, 2);
        b.HasIndex(x => new { x.ProductKey, x.Date }).IsUnique();
    }
}
