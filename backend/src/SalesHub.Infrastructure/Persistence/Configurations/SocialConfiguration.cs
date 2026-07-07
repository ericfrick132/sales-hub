using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities.Social;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class PostingProfileConfiguration : IEntityTypeConfiguration<PostingProfile>
{
    public void Configure(EntityTypeBuilder<PostingProfile> b)
    {
        b.ToTable("posting_profiles");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.ProductKey).IsUnique();
        b.Property(x => x.BrandColorsJson).HasColumnType("jsonb");
        b.Property(x => x.BufferChannelsJson).HasColumnType("jsonb");
        b.Property(x => x.BrandFonts).HasMaxLength(256);
        b.Property(x => x.BrandLogoUrl).HasMaxLength(512);
        b.Property(x => x.BrandVoice).HasColumnType("text");
        b.Property(x => x.BrandGuidelines).HasColumnType("text");
        b.Property(x => x.TargetAudience).HasMaxLength(512);
        b.Property(x => x.ContentPillars).HasColumnType("text[]");
        b.Property(x => x.PostHours).HasColumnType("integer[]");
        b.Property(x => x.PostDays).HasColumnType("integer[]");
        b.Property(x => x.LandingUrl).HasMaxLength(1024);
        b.Property(x => x.LandingKnowledge).HasColumnType("text");
    }
}

public class PostingChannelConfiguration : IEntityTypeConfiguration<PostingChannel>
{
    public void Configure(EntityTypeBuilder<PostingChannel> b)
    {
        b.ToTable("posting_channels");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Platform).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.Format).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.AssetKind).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.Distribution).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.WarmrAccount).HasMaxLength(128);
        b.Property(x => x.BufferChannelId).HasMaxLength(64);
        b.Property(x => x.PromptTemplate).HasColumnType("text");
        b.HasIndex(x => new { x.ProductKey, x.Platform }).IsUnique()
            .HasDatabaseName("IX_PostingChannel_Product_Platform");
    }
}

public class SocialPostConfiguration : IEntityTypeConfiguration<SocialPost>
{
    public void Configure(EntityTypeBuilder<SocialPost> b)
    {
        b.ToTable("social_posts");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        // Enums como string para legibilidad en la DB (snake_case naming aplica a columnas).
        b.Property(x => x.Platform).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.Format).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.AssetKind).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.Target).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.WarmrAccount).HasMaxLength(128);
        b.Property(x => x.BufferChannelId).HasMaxLength(64);
        b.Property(x => x.ContentPillar).HasMaxLength(128);
        b.Property(x => x.PostType).HasMaxLength(32);
        b.Property(x => x.Concept).HasColumnType("text");
        b.Property(x => x.Prompt).HasColumnType("text");
        b.Property(x => x.Caption).HasColumnType("text");
        b.Property(x => x.Hashtags).HasColumnType("text[]");
        b.Property(x => x.AssetUrl).HasMaxLength(1024);
        b.Property(x => x.ThumbnailUrl).HasMaxLength(1024);
        b.Property(x => x.BufferPostId).HasMaxLength(64);
        b.Property(x => x.Error).HasColumnType("text");
        b.Property(x => x.GenerationModel).HasMaxLength(64);
        b.Property(x => x.RawJson).HasColumnType("text");
        b.HasIndex(x => new { x.ProductKey, x.Status });
        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.CreatedAt);
        b.HasIndex(x => x.InspirationPostId);
    }
}

public class SocialPostAssetConfiguration : IEntityTypeConfiguration<SocialPostAsset>
{
    public void Configure(EntityTypeBuilder<SocialPostAsset> b)
    {
        b.ToTable("social_post_assets");
        b.HasKey(x => x.Id);
        b.Property(x => x.MimeType).HasMaxLength(128).IsRequired();
        b.Property(x => x.Content).HasColumnType("bytea").IsRequired();
        b.HasIndex(x => x.SocialPostId);
    }
}

public class InspirationItemConfiguration : IEntityTypeConfiguration<InspirationItem>
{
    public void Configure(EntityTypeBuilder<InspirationItem> b)
    {
        b.ToTable("inspiration_items");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64);
        b.Property(x => x.Topic).HasMaxLength(128).IsRequired();
        b.Property(x => x.Note).HasColumnType("text");
        b.Property(x => x.SourceUrl).HasMaxLength(1024);
        b.Property(x => x.MimeType).HasMaxLength(128);
        b.Property(x => x.ImageContent).HasColumnType("bytea");
        b.HasIndex(x => x.Topic);
        b.HasIndex(x => x.ProductKey);
        b.HasIndex(x => x.CreatedAt);
        b.HasIndex(x => x.PendingTopic);
    }
}

public class InspirationSettingsConfiguration : IEntityTypeConfiguration<InspirationSettings>
{
    public void Configure(EntityTypeBuilder<InspirationSettings> b)
    {
        b.ToTable("inspiration_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.InstanceName).HasMaxLength(128);
        b.Property(x => x.MasterPhone).HasMaxLength(32);
    }
}
