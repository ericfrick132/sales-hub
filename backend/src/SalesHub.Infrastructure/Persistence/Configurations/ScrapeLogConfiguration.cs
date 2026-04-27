using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class ScrapeLogConfiguration : IEntityTypeConfiguration<ScrapeLog>
{
    public void Configure(EntityTypeBuilder<ScrapeLog> b)
    {
        b.ToTable("scrape_log");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Country).HasMaxLength(4);
        b.Property(x => x.City).HasMaxLength(128);
        b.Property(x => x.Category).HasMaxLength(128);
        b.Property(x => x.Source).HasConversion<int>();
        b.Property(x => x.Status).HasMaxLength(16);
        b.Property(x => x.Error).HasColumnType("text");
        b.HasIndex(x => new { x.ProductKey, x.City, x.Category, x.RunAt });
    }
}
