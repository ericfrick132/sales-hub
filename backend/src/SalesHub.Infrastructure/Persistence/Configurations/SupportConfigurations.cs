using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class ToneProfileConfiguration : IEntityTypeConfiguration<ToneProfile>
{
    public void Configure(EntityTypeBuilder<ToneProfile> b)
    {
        b.ToTable("tone_profiles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Scope).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.Scope).IsUnique();
        b.Property(x => x.Content).IsRequired();
    }
}

public class ProductFaqConfiguration : IEntityTypeConfiguration<ProductFaq>
{
    public void Configure(EntityTypeBuilder<ProductFaq> b)
    {
        b.ToTable("product_faqs");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.ProductKey);
        b.Property(x => x.Question).HasMaxLength(500).IsRequired();
        b.Property(x => x.Keywords).HasColumnType("text[]");
        b.Property(x => x.Answer).IsRequired();
        b.Property(x => x.Source).HasMaxLength(16);
    }
}

public class ProductKnowledgeConfiguration : IEntityTypeConfiguration<ProductKnowledge>
{
    public void Configure(EntityTypeBuilder<ProductKnowledge> b)
    {
        b.ToTable("product_knowledge");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.ProductKey).IsUnique();
        b.Property(x => x.Content).IsRequired();
        b.Property(x => x.SourceVersion).HasMaxLength(64);
    }
}

public class SupportCaseConfiguration : IEntityTypeConfiguration<SupportCase>
{
    public void Configure(EntityTypeBuilder<SupportCase> b)
    {
        b.ToTable("support_cases");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Summary).HasMaxLength(500);
        b.HasIndex(x => new { x.LeadId, x.Status });
        b.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
    }
}
