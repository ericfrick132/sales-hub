using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities.Promos;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class PromoEventConfiguration : IEntityTypeConfiguration<PromoEvent>
{
    public void Configure(EntityTypeBuilder<PromoEvent> b)
    {
        b.ToTable("promo_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.Slug).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Slug).IsUnique();
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasColumnType("text");
        b.Property(x => x.Apps).HasColumnType("text[]");
        b.Property(x => x.CouponCode).HasMaxLength(40);
        b.Property(x => x.Confidence).HasMaxLength(20);
        b.Property(x => x.Source).HasMaxLength(1024);
        b.Property(x => x.Note).HasColumnType("text");
        b.Property(x => x.ImageSourceUrl).HasMaxLength(1024);
        b.Property(x => x.ImageCredit).HasMaxLength(300);
        b.HasIndex(x => x.Date);
    }
}

public class PromoCampaignConfiguration : IEntityTypeConfiguration<PromoCampaign>
{
    public void Configure(EntityTypeBuilder<PromoCampaign> b)
    {
        b.ToTable("promo_campaigns");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.CouponCode).HasMaxLength(40);
        b.Property(x => x.Headline).HasMaxLength(300);
        b.Property(x => x.Subheadline).HasColumnType("text");
        b.Property(x => x.Bullets).HasColumnType("text[]");
        b.Property(x => x.LandingUrl).HasMaxLength(512);
        b.Property(x => x.TrackedUrl).HasMaxLength(1024);
        b.Property(x => x.Error).HasColumnType("text");
        b.HasOne(x => x.Event).WithMany(e => e.Campaigns).HasForeignKey(x => x.PromoEventId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.PromoEventId, x.ProductKey }).IsUnique();
        b.HasIndex(x => new { x.ProductKey, x.Status });
    }
}
