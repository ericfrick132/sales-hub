using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class LocalityConfiguration : IEntityTypeConfiguration<Locality>
{
    public void Configure(EntityTypeBuilder<Locality> b)
    {
        b.ToTable("localities");
        b.HasKey(x => x.Gid2);

        b.Property(x => x.Gid2).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(160).IsRequired();
        b.Property(x => x.AdminLevel1Gid).HasMaxLength(32).IsRequired();
        b.Property(x => x.AdminLevel1Name).HasMaxLength(160).IsRequired();
        b.Property(x => x.CountryCode).HasMaxLength(4).IsRequired();
        b.Property(x => x.CountryName).HasMaxLength(80).IsRequired();

        b.HasIndex(x => x.CountryCode);
        b.HasIndex(x => new { x.CountryCode, x.AdminLevel1Gid });
    }
}

public class SellerLocalityConfiguration : IEntityTypeConfiguration<SellerLocality>
{
    public void Configure(EntityTypeBuilder<SellerLocality> b)
    {
        b.ToTable("seller_localities");
        b.HasKey(x => new { x.SellerId, x.LocalityGid2 });

        b.Property(x => x.LocalityGid2).HasMaxLength(32);

        b.HasOne(x => x.Seller)
            .WithMany(s => s.LocalityAssignments)
            .HasForeignKey(x => x.SellerId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.Locality)
            .WithMany(l => l.SellerAssignments)
            .HasForeignKey(x => x.LocalityGid2)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.LocalityGid2);
    }
}
