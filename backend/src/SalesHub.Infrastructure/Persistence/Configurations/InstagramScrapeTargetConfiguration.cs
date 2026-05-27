using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class InstagramScrapeTargetConfiguration : IEntityTypeConfiguration<InstagramScrapeTarget>
{
    public void Configure(EntityTypeBuilder<InstagramScrapeTarget> b)
    {
        b.ToTable("instagram_scrape_targets");
        b.HasKey(x => x.Id);

        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.TargetType).HasMaxLength(32).IsRequired();
        b.Property(x => x.TargetValue).HasMaxLength(256).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(256);

        b.HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductKey)
            .HasPrincipalKey(p => p.ProductKey)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.AssignedAccount)
            .WithMany()
            .HasForeignKey(x => x.AssignedAccountId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(x => x.ProductKey);
        b.HasIndex(x => x.AssignedAccountId);
    }
}
