using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class MetaAdConfiguration : IEntityTypeConfiguration<MetaAd>
{
    public void Configure(EntityTypeBuilder<MetaAd> b)
    {
        b.ToTable("meta_ads");
        b.HasKey(x => x.AdId);
        b.Property(x => x.AdId).HasMaxLength(40);
        b.Property(x => x.AccountId).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(256);
        b.Property(x => x.Status).HasMaxLength(40);
        b.Property(x => x.ProductKey).HasMaxLength(64);
        b.Property(x => x.Rubro).HasMaxLength(80);
        b.Property(x => x.Angle).HasMaxLength(80);
        b.Property(x => x.BusinessRef).HasMaxLength(120);
        b.Property(x => x.PrefillText).HasMaxLength(512);
        b.Property(x => x.Title).HasMaxLength(256);
        b.Property(x => x.DestinationPhone).HasMaxLength(32);
        b.HasIndex(x => x.ProductKey);
    }
}
