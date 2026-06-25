using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class LeadOnboardingConfiguration : IEntityTypeConfiguration<LeadOnboarding>
{
    public void Configure(EntityTypeBuilder<LeadOnboarding> b)
    {
        b.ToTable("lead_onboardings");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.LeadId).IsUnique();
        b.Property(x => x.ContactName).HasMaxLength(160);
        b.Property(x => x.GymName).HasMaxLength(160);
        b.Property(x => x.MemberCount).HasMaxLength(120);
        b.Property(x => x.PaymentMethod).HasMaxLength(200);
        b.Property(x => x.Email).HasMaxLength(160);
        b.Property(x => x.AccessUrl).HasMaxLength(500);
    }
}
