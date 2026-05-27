using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class InstagramAccountConfiguration : IEntityTypeConfiguration<InstagramAccount>
{
    public void Configure(EntityTypeBuilder<InstagramAccount> b)
    {
        b.ToTable("instagram_accounts");
        b.HasKey(x => x.Id);

        b.Property(x => x.Username).HasMaxLength(128).IsRequired();
        b.Property(x => x.EncryptedPassword).HasMaxLength(512).IsRequired();
        b.Property(x => x.TwoFactorSecret).HasMaxLength(64);
        b.Property(x => x.SessionCookiesJson).HasColumnType("text");
        b.Property(x => x.IsAwaitingTwoFactor).HasDefaultValue(false);

        b.HasOne(x => x.Seller)
            .WithMany()
            .HasForeignKey(x => x.SellerId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.SellerId);
    }
}
