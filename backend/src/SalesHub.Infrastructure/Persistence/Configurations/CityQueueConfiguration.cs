using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class CityQueueConfiguration : IEntityTypeConfiguration<CityQueue>
{
    public void Configure(EntityTypeBuilder<CityQueue> b)
    {
        b.ToTable("cities_queue");
        b.HasKey(x => x.Id);
        b.Property(x => x.Country).HasMaxLength(4).IsRequired();
        b.Property(x => x.Province).HasMaxLength(128).IsRequired();
        b.Property(x => x.City).HasMaxLength(128).IsRequired();
        b.Property(x => x.PopulationBucket).HasConversion<int>();
        b.HasIndex(x => new { x.Country, x.Province, x.City }).IsUnique();
        b.HasIndex(x => x.GeonameId);
        b.HasIndex(x => new { x.Country, x.PopulationBucket });
    }
}
