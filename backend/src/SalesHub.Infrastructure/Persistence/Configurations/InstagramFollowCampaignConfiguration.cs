using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class InstagramFollowCampaignConfiguration : IEntityTypeConfiguration<InstagramFollowCampaign>
{
    public void Configure(EntityTypeBuilder<InstagramFollowCampaign> b)
    {
        b.ToTable("instagram_follow_campaigns");
        b.HasKey(x => x.Id);

        b.Property(x => x.SourceHandle).HasMaxLength(64).IsRequired();
        b.Property(x => x.DailyRate).HasDefaultValue(30);
        b.Property(x => x.ScrapeBatchSize).HasDefaultValue(100);
        b.Property(x => x.MinQueuedThreshold).HasDefaultValue(20);
        b.Property(x => x.IsActive).HasDefaultValue(true);

        b.HasOne(x => x.InstagramAccount)
            .WithMany()
            .HasForeignKey(x => x.InstagramAccountId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.Actions)
            .WithOne(a => a.Campaign)
            .HasForeignKey(a => a.CampaignId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.InstagramAccountId);
        b.HasIndex(x => x.IsActive);
    }
}

public class InstagramFollowActionConfiguration : IEntityTypeConfiguration<InstagramFollowAction>
{
    public void Configure(EntityTypeBuilder<InstagramFollowAction> b)
    {
        b.ToTable("instagram_follow_actions");
        b.HasKey(x => x.Id);

        b.Property(x => x.TargetHandle).HasMaxLength(64).IsRequired();
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.ErrorMessage).HasMaxLength(1024);

        b.HasIndex(x => new { x.CampaignId, x.Status });
        b.HasIndex(x => new { x.InstagramAccountId, x.ExecutedAt });
        // Dedup dentro de la misma campaña
        b.HasIndex(x => new { x.CampaignId, x.TargetHandle }).IsUnique();
    }
}
