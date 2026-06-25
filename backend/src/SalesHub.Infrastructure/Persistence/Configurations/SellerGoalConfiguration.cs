using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class SellerGoalConfiguration : IEntityTypeConfiguration<SellerGoal>
{
    public void Configure(EntityTypeBuilder<SellerGoal> b)
    {
        b.ToTable("seller_goals");
        b.HasKey(x => x.Id);
        b.Property(x => x.Metric).HasConversion<int>();
        b.Property(x => x.Period).HasConversion<int>();
        b.Property(x => x.ProductKey).HasMaxLength(64);
        b.HasOne(x => x.Seller)
            .WithMany()
            .HasForeignKey(x => x.SellerId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.SellerId, x.ProductKey, x.Metric, x.Period });
    }
}
