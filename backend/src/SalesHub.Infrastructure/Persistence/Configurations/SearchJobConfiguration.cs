using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class SearchJobConfiguration : IEntityTypeConfiguration<SearchJob>
{
    public void Configure(EntityTypeBuilder<SearchJob> b)
    {
        b.ToTable("search_jobs");
        b.HasKey(x => x.Id);

        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.LocalityGid2).HasMaxLength(32);
        b.Property(x => x.Category).HasMaxLength(120);
        b.Property(x => x.Query).HasMaxLength(400).IsRequired();
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.Error).HasColumnType("text");

        b.HasOne(x => x.Seller).WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductKey)
            .HasPrincipalKey(p => p.ProductKey).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Locality).WithMany().HasForeignKey(x => x.LocalityGid2).OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(x => new { x.Status, x.ScheduledAt });
        b.HasIndex(x => new { x.SellerId, x.ScheduledAt });
    }
}
