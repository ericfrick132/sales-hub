using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class CompetitorConfiguration : IEntityTypeConfiguration<Competitor>
{
    public void Configure(EntityTypeBuilder<Competitor> b)
    {
        b.ToTable("competitors");
        b.HasKey(x => x.Id);
        b.Property(x => x.Handle).HasMaxLength(128).IsRequired();
        b.Property(x => x.Platform).HasMaxLength(32).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(256);
        b.Property(x => x.Vertical).HasMaxLength(64);
        b.Property(x => x.RawProfileJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.Platform, x.Handle }).IsUnique();
    }
}

public class CompetitorPostConfiguration : IEntityTypeConfiguration<CompetitorPost>
{
    public void Configure(EntityTypeBuilder<CompetitorPost> b)
    {
        b.ToTable("competitor_posts");
        b.HasKey(x => x.Id);
        b.Property(x => x.ExternalPostId).HasMaxLength(128).IsRequired();
        b.Property(x => x.PostUrl).HasMaxLength(512);
        b.Property(x => x.Caption).HasColumnType("text");
        b.Property(x => x.Hashtags).HasColumnType("text[]");
        b.Property(x => x.RawJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.CompetitorId, x.ExternalPostId }).IsUnique();
        b.HasOne(x => x.Competitor)
            .WithMany(x => x.Posts)
            .HasForeignKey(x => x.CompetitorId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class CompetitorCommentConfiguration : IEntityTypeConfiguration<CompetitorComment>
{
    public void Configure(EntityTypeBuilder<CompetitorComment> b)
    {
        b.ToTable("competitor_comments");
        b.HasKey(x => x.Id);
        b.Property(x => x.AuthorHandle).HasMaxLength(128);
        b.Property(x => x.Text).HasColumnType("text");
        b.Property(x => x.RawJson).HasColumnType("jsonb");
        b.HasIndex(x => x.PostId);
        b.HasIndex(x => x.IsNegative);
        b.HasOne(x => x.Post)
            .WithMany(x => x.Comments)
            .HasForeignKey(x => x.PostId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
