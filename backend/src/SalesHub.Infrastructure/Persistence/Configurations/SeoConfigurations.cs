using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class SeoSiteConfiguration : IEntityTypeConfiguration<SeoSite>
{
    public void Configure(EntityTypeBuilder<SeoSite> b)
    {
        b.ToTable("seo_sites");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.Property(x => x.Domain).HasMaxLength(256);
        b.Property(x => x.Language).HasMaxLength(8).IsRequired();
        b.Property(x => x.Sector).HasMaxLength(512);
        b.Property(x => x.Audience).HasColumnType("text");
        b.Property(x => x.ProductSummary).HasColumnType("text");
        b.Property(x => x.BrandVoice).HasColumnType("text");
        b.Property(x => x.TargetCountries).HasColumnType("text[]");
        b.Property(x => x.BlogBaseUrl).HasMaxLength(512);
        b.HasIndex(x => x.ProductKey);
        b.HasIndex(x => x.Domain);
    }
}

public class SeoKeywordConfiguration : IEntityTypeConfiguration<SeoKeyword>
{
    public void Configure(EntityTypeBuilder<SeoKeyword> b)
    {
        b.ToTable("seo_keywords");
        b.HasKey(x => x.Id);
        b.Property(x => x.Term).HasMaxLength(256).IsRequired();
        b.Property(x => x.Cluster).HasMaxLength(128);
        b.Property(x => x.Source).HasMaxLength(32);
        b.HasIndex(x => new { x.SiteId, x.Term }).IsUnique();
        b.HasIndex(x => x.Status);
        b.HasOne(x => x.Site)
            .WithMany(x => x.Keywords)
            .HasForeignKey(x => x.SiteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class SeoArticleConfiguration : IEntityTypeConfiguration<SeoArticle>
{
    public void Configure(EntityTypeBuilder<SeoArticle> b)
    {
        b.ToTable("seo_articles");
        b.HasKey(x => x.Id);
        b.Property(x => x.TargetKeyword).HasMaxLength(256);
        b.Property(x => x.Title).HasMaxLength(512).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(512);
        b.Property(x => x.MetaDescription).HasMaxLength(1024);
        b.Property(x => x.BodyMarkdown).HasColumnType("text");
        b.Property(x => x.FaqJson).HasColumnType("jsonb");
        b.Property(x => x.JsonLd).HasColumnType("text");
        b.Property(x => x.OptimizationNotes).HasColumnType("text");
        b.Property(x => x.GeneratedBy).HasMaxLength(64);
        b.HasIndex(x => new { x.SiteId, x.Status });
        b.HasOne(x => x.Site)
            .WithMany(x => x.Articles)
            .HasForeignKey(x => x.SiteId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Keyword)
            .WithMany()
            .HasForeignKey(x => x.KeywordId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
