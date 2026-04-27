using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class SellerConfiguration : IEntityTypeConfiguration<Seller>
{
    public void Configure(EntityTypeBuilder<Seller> b)
    {
        b.ToTable("sellers");
        b.HasKey(x => x.Id);

        b.Property(x => x.SellerKey).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.SellerKey).IsUnique();

        b.Property(x => x.Email).HasMaxLength(256).IsRequired();
        b.HasIndex(x => x.Email).IsUnique();

        b.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        b.Property(x => x.PasswordHash).HasMaxLength(256);
        b.Property(x => x.GoogleSubject).HasMaxLength(128);
        b.Property(x => x.WhatsappPhone).HasMaxLength(32);
        b.Property(x => x.Timezone).HasMaxLength(64);

        b.Property(x => x.Role).HasConversion<int>();
        b.Property(x => x.SendMode).HasConversion<int>();

        b.Property(x => x.VerticalsWhitelist)
            .HasColumnType("text[]");
        b.Property(x => x.RegionsAssigned)
            .HasColumnType("text[]");

        b.HasOne(x => x.EvolutionInstance)
            .WithOne(x => x.Seller!)
            .HasForeignKey<EvolutionInstance>(x => x.SellerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
