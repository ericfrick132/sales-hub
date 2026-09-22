using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class EvolutionInstanceConfiguration : IEntityTypeConfiguration<EvolutionInstance>
{
    public void Configure(EntityTypeBuilder<EvolutionInstance> b)
    {
        b.ToTable("evolution_instances");
        b.HasKey(x => x.Id);
        b.Property(x => x.InstanceName).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.InstanceName).IsUnique();
        b.HasIndex(x => x.SellerId).IsUnique();
        b.Property(x => x.Label).HasMaxLength(80);
        b.Property(x => x.ProductKey).HasMaxLength(64);
        // Sin unique: dos teléfonos pueden atender la misma app (se agregan desde /devices).
        b.HasIndex(x => x.ProductKey);
        // Las otras apps que atiende el mismo número (un celu recibe consultas de varias).
        b.Property(x => x.ExtraProductKeys)
            .HasColumnType("text[]")
            .HasDefaultValueSql("'{}'::text[]");
        // Prospectos del historial: a quiénes se les reparten los contactos del teléfono y qué
        // palabras dejan un chat afuera. Default '{}' para que la columna entre en una tabla
        // con filas (NOT NULL sin default no se puede agregar en Postgres).
        b.Property(x => x.ProspectSellerIds)
            .HasColumnType("uuid[]")
            .HasDefaultValueSql("'{}'::uuid[]");
        b.Property(x => x.ProspectSkipWords)
            .HasColumnType("text[]")
            .HasDefaultValueSql("'{}'::text[]");
        b.Property(x => x.ConnectedPhoneNumber).HasMaxLength(32);
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.LastQrCodeBase64).HasColumnType("text");
    }
}
