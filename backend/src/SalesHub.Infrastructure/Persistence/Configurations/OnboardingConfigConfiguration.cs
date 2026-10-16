using System.Text.Json;
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
        // Pre-calificación: 2 preguntas por defecto (también para las apps que ya existían).
        b.Property(x => x.HandoffAfterQuestions).HasDefaultValue(2);
        b.Property(x => x.PresentAs).HasMaxLength(40);
        b.Property(x => x.DemoPitch).HasDefaultValue(string.Empty);
        b.Property(x => x.ProductKey).HasMaxLength(60);
        b.Property(x => x.ProvisionNameField).HasMaxLength(60);
        // Questions (List<string>) → text[] nativo de Npgsql, sin converter.
        // Default explícito: ADD COLUMN NOT NULL de un array sin default revienta con 23502
        // si la tabla tiene filas (nos pasó con reengage_media_asset_ids, 2026-07-20).
        b.Property(x => x.ReengageMediaCaptions).HasDefaultValueSql("'{}'");
        b.Property(x => x.FollowUpMessages).HasDefaultValueSql("'{}'");
        b.Property(x => x.KnownBusinessQuestion).HasDefaultValue(string.Empty);
        b.Property(x => x.KnownBusinessGreeting).HasDefaultValue(string.Empty);
        b.Property(x => x.PricesMessage).HasDefaultValue(string.Empty);
        b.Property(x => x.StartQuestion).HasDefaultValue(string.Empty);
        b.Property(x => x.DemoOffer).HasDefaultValue(string.Empty);
        b.Property(x => x.DemoLink).HasDefaultValue(string.Empty);
        b.Property(x => x.PricesMediaAssetIds).HasDefaultValueSql("'{}'");
        b.Property(x => x.AnglePitches)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v ?? new(), (JsonSerializerOptions?)null),
                v => DeserializePitches(v));
    }

    private static List<AnglePitch> DeserializePitches(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return new();
        try { return JsonSerializer.Deserialize<List<AnglePitch>>(v, (JsonSerializerOptions?)null) ?? new(); }
        catch { return new(); }
    }
}
