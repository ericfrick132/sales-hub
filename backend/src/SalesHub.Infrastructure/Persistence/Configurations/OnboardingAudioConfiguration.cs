using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class OnboardingAudioConfiguration : IEntityTypeConfiguration<OnboardingAudio>
{
    public void Configure(EntityTypeBuilder<OnboardingAudio> b)
    {
        b.ToTable("onboarding_audios");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.ProductKey);
        b.Property(x => x.ProductKey).HasMaxLength(60);
        b.Property(x => x.MimeType).HasMaxLength(60);
    }
}
