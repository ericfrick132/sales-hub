using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class FollowupSequenceConfiguration : IEntityTypeConfiguration<FollowupSequence>
{
    public void Configure(EntityTypeBuilder<FollowupSequence> b)
    {
        b.ToTable("followup_sequences");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Trigger).HasMaxLength(64).IsRequired();
        b.HasIndex(x => new { x.ProductKey, x.Trigger }).IsUnique();

        // jsonb: sumar campos al paso (variantes, condiciones) no requiere migration.
        b.Property(x => x.Steps)
            .HasColumnName("steps")
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v ?? new(), (JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v) || !v.TrimStart().StartsWith('[')
                    ? new List<FollowupStep>()
                    : JsonSerializer.Deserialize<List<FollowupStep>>(v, (JsonSerializerOptions?)null) ?? new());

        // BACKLOG (abandonos viejos): mensajes configurables por app, fuera de los pasos del
        // flujo en vivo. Nullables → la app cae a su literal hardcodeado si están vacíos.
        b.Property(x => x.BacklogColdMessage).HasColumnName("backlog_cold_message");
        b.Property(x => x.BacklogWarmMessage).HasColumnName("backlog_warm_message");
        b.Property(x => x.BacklogColdAfterDays).HasColumnName("backlog_cold_after_days");
    }
}
