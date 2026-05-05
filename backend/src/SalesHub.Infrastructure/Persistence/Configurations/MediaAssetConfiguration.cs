using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> b)
    {
        b.ToTable("media_assets");
        b.HasKey(x => x.Id);

        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.ProductKey);

        b.Property(x => x.FileName).HasMaxLength(256).IsRequired();
        b.Property(x => x.MimeType).HasMaxLength(128).IsRequired();
        b.Property(x => x.SizeBytes);
        b.Property(x => x.Content).HasColumnType("bytea").IsRequired();

        b.HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductKey)
            .HasPrincipalKey(p => p.ProductKey)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
