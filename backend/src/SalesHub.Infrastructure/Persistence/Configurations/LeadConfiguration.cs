using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> b)
    {
        b.ToTable("leads");
        b.HasKey(x => x.Id);

        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Name).HasMaxLength(256).IsRequired();
        b.Property(x => x.Address).HasMaxLength(512);
        b.Property(x => x.City).HasMaxLength(128);
        b.Property(x => x.Province).HasMaxLength(128);
        b.Property(x => x.Country).HasMaxLength(4);
        b.Property(x => x.RawPhone).HasMaxLength(64);
        b.Property(x => x.WhatsappPhone).HasMaxLength(32);
        b.Property(x => x.WhatsappJid).HasMaxLength(64);
        b.Property(x => x.Website).HasMaxLength(512);
        b.Property(x => x.InstagramHandle).HasMaxLength(128);
        b.Property(x => x.FacebookUrl).HasMaxLength(256);
        b.Property(x => x.BusinessStatus).HasMaxLength(32);
        b.Property(x => x.PlaceId).HasMaxLength(128);
        b.Property(x => x.ExternalId).HasMaxLength(128);
        b.Property(x => x.SearchQuery).HasMaxLength(256);
        b.Property(x => x.RawDataJson).HasColumnType("jsonb");
        b.Property(x => x.RenderedMessage).HasColumnType("text");
        b.Property(x => x.WhatsappLink).HasColumnType("text");
        b.Property(x => x.Notes).HasColumnType("text");

        b.Property(x => x.Source).HasConversion<int>();
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.Types).HasColumnType("text[]");
        b.Property(x => x.LocalityGid2).HasMaxLength(32);

        b.HasIndex(x => new { x.ProductKey, x.WhatsappPhone })
            .IsUnique()
            .HasFilter("whatsapp_phone IS NOT NULL");

        b.HasIndex(x => new { x.ProductKey, x.PlaceId })
            .HasFilter("place_id IS NOT NULL");

        b.HasIndex(x => new { x.SellerId, x.Status });
        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.CreatedAt);
        b.HasIndex(x => new { x.Latitude, x.Longitude });

        b.HasOne(x => x.Product)
            .WithMany(x => x.Leads)
            .HasForeignKey(x => x.ProductKey)
            .HasPrincipalKey(p => p.ProductKey)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Seller)
            .WithMany(x => x.Leads)
            .HasForeignKey(x => x.SellerId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasOne(x => x.Locality)
            .WithMany()
            .HasForeignKey(x => x.LocalityGid2)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(x => new { x.ProductKey, x.LocalityGid2 });
    }
}
