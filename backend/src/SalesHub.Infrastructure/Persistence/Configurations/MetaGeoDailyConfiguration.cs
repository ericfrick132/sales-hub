using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class MetaGeoDailyConfiguration : IEntityTypeConfiguration<MetaGeoDaily>
{
    public void Configure(EntityTypeBuilder<MetaGeoDaily> b)
    {
        b.ToTable("meta_geo_daily");
        b.HasKey(x => x.Id);
        b.Property(x => x.AccountId).HasMaxLength(40);
        b.Property(x => x.CampaignId).HasMaxLength(40);
        b.Property(x => x.CampaignName).HasMaxLength(256);
        b.Property(x => x.AdsetId).HasMaxLength(40);
        b.Property(x => x.AdsetName).HasMaxLength(256);
        b.Property(x => x.AdId).HasMaxLength(40);
        b.Property(x => x.AdName).HasMaxLength(256);
        b.Property(x => x.ProductKey).HasMaxLength(64);
        b.Property(x => x.Country).HasMaxLength(8);
        b.Property(x => x.Region).HasMaxLength(120);
        b.Property(x => x.Spend).HasPrecision(14, 4);
        b.HasIndex(x => new { x.Date, x.AdId, x.Region }).IsUnique();
        b.HasIndex(x => new { x.AccountId, x.Date });
        b.HasIndex(x => x.ProductKey);
    }
}
