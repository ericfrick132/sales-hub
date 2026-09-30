using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class RemarketingSettingsConfiguration : IEntityTypeConfiguration<RemarketingSettings>
{
    public void Configure(EntityTypeBuilder<RemarketingSettings> b)
    {
        b.ToTable("remarketing_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever(); // fila única (Id = 1)
        b.Property(x => x.SenderSellerIds).HasDefaultValueSql("'{}'");
        b.Property(x => x.ProductKeys).HasDefaultValueSql("'{}'");
    }
}

public class RemarketingAttemptConfiguration : IEntityTypeConfiguration<RemarketingAttempt>
{
    public void Configure(EntityTypeBuilder<RemarketingAttempt> b)
    {
        b.ToTable("remarketing_attempts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Stage).HasMaxLength(40).IsRequired();
        b.Property(x => x.Message).IsRequired();
        b.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.LeadId);
        b.HasIndex(x => new { x.SellerId, x.EnqueuedAt });
    }
}

public class ReplyIntentConfiguration : IEntityTypeConfiguration<ReplyIntent>
{
    public void Configure(EntityTypeBuilder<ReplyIntent> b)
    {
        b.ToTable("reply_intents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.Key).IsUnique();
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Pattern).IsRequired();
        b.Property(x => x.Action).HasMaxLength(40).IsRequired();
        b.Property(x => x.Examples).HasDefaultValueSql("'{}'");
        b.Property(x => x.ReplyByProduct)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new Dictionary<string, string>()
                    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null)!,
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<Dictionary<string, string>>(
                    (a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null) == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode(),
                    v => new Dictionary<string, string>(v)))
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb");
    }
}
