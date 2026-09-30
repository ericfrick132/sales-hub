using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class RemarketingSettingsConfiguration : IEntityTypeConfiguration<RemarketingSettings>
{
    public void Configure(EntityTypeBuilder<RemarketingSettings> b)
    {
        b.ToTable("remarketing_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever(); // fila única (Id = 1)
        b.Property(x => x.SenderSellerIds).HasDefaultValueSql("'{}'");
        b.Property(x => x.ProductKeys).HasDefaultValueSql("'{}'");
    }
}

public class RemarketingAttemptConfiguration : IEntityTypeConfiguration<RemarketingAttempt>
{
    public void Configure(EntityTypeBuilder<RemarketingAttempt> b)
    {
        b.ToTable("remarketing_attempts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Stage).HasMaxLength(40).IsRequired();
        b.Property(x => x.Message).IsRequired();
        b.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.LeadId);
        b.HasIndex(x => new { x.SellerId, x.EnqueuedAt });
    }
}
