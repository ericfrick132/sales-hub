using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class AiRuleConfiguration : IEntityTypeConfiguration<AiRule>
{
    public void Configure(EntityTypeBuilder<AiRule> b)
    {
        b.ToTable("ai_rules");
        b.HasKey(x => x.Id);
        b.Property(x => x.Text).IsRequired().HasMaxLength(2000);
        b.Property(x => x.ProductKey).HasMaxLength(64);
        b.HasIndex(x => x.IsActive);
    }
}
