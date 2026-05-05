using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("products");
        b.HasKey(x => x.Id);

        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.ProductKey).IsUnique();

        b.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        b.Property(x => x.Country).HasMaxLength(4).IsRequired();
        b.Property(x => x.CountryName).HasMaxLength(64).IsRequired();
        b.Property(x => x.RegionCode).HasMaxLength(8).IsRequired();
        b.Property(x => x.Language).HasMaxLength(8).IsRequired();
        b.Property(x => x.PhonePrefix).HasMaxLength(8).IsRequired();
        b.Property(x => x.CheckoutUrl).HasMaxLength(256);
        b.Property(x => x.PriceDisplay).HasMaxLength(64);
        b.Property(x => x.MessageTemplate).HasColumnType("text");
        b.Property(x => x.OpenerTemplate).HasColumnType("text");

        b.Property(x => x.Categories).HasColumnType("text[]");
        b.Property(x => x.ReplyTemplates).HasColumnType("text[]");
        b.Property(x => x.TriggerHours).HasColumnType("integer[]");

        // jsonb para flexibilidad: agregar campos al MessageStep en el futuro
        // (variantes A/B, condiciones) no requiere otra migration.
        b.Property(x => x.MessageSteps)
            .HasColumnName("message_steps")
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v ?? new(), (JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new List<MessageStep>()
                    : JsonSerializer.Deserialize<List<MessageStep>>(v, (JsonSerializerOptions?)null) ?? new());
    }
}
