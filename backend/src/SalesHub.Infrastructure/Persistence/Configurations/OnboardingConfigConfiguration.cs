using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class OnboardingConfigConfiguration : IEntityTypeConfiguration<OnboardingConfig>
{
    public void Configure(EntityTypeBuilder<OnboardingConfig> b)
    {
        b.ToTable("onboarding_configs");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.ProductKey).IsUnique();
        b.Property(x => x.ProductKey).HasMaxLength(60);
        b.Property(x => x.ProvisionNameField).HasMaxLength(60);
        // Questions (List<string>) → text[] nativo de Npgsql, sin converter.
    }
}
