using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class PitchConfiguration : IEntityTypeConfiguration<Pitch>
{
    public void Configure(EntityTypeBuilder<Pitch> b)
    {
        b.ToTable("pitches");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Name).HasMaxLength(160).IsRequired();
        b.Property(x => x.TriggerText).HasMaxLength(256);
        b.Property(x => x.AutoTagOnReply).HasMaxLength(64);
        b.Property(x => x.StatusOnReply).HasMaxLength(32);
        b.Property(x => x.Channel).HasConversion<int>();
        b.Property(x => x.AdIds).HasColumnType("text[]").HasDefaultValueSql("'{}'");
        // jsonb: agregar campos a los pasos no requiere migración.
        b.Property(x => x.Steps)
            .HasColumnName("steps")
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v ?? new(), (JsonSerializerOptions?)null),
                v => Deserialize(v));
        b.HasIndex(x => new { x.ProductKey, x.Active });
        b.HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductKey)
            .HasPrincipalKey(p => p.ProductKey)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static List<PitchStep> Deserialize(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return new();
        try { return JsonSerializer.Deserialize<List<PitchStep>>(v, (JsonSerializerOptions?)null) ?? new(); }
        catch { return new(); }
    }
}

public class LeadPitchStateConfiguration : IEntityTypeConfiguration<LeadPitchState>
{
    public void Configure(EntityTypeBuilder<LeadPitchState> b)
    {
        b.ToTable("lead_pitch_states");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.LeadId).IsUnique();
        b.HasIndex(x => new { x.PitchId, x.CompletedAt });
        b.HasIndex(x => x.NextStepDueAt);
        b.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Pitch).WithMany().HasForeignKey(x => x.PitchId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ConversationFeedbackConfiguration : IEntityTypeConfiguration<ConversationFeedback>
{
    public void Configure(EntityTypeBuilder<ConversationFeedback> b)
    {
        b.ToTable("conversation_feedbacks");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Note).HasColumnType("text");
        b.Property(x => x.RatedMessage).HasColumnType("text");
        b.HasIndex(x => new { x.ProductKey, x.CreatedAt });
        b.HasIndex(x => x.LeadId);
        b.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Seller).WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.SetNull);
    }
}
