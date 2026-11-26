using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class MetaConversionEventConfiguration : IEntityTypeConfiguration<MetaConversionEvent>
{
    public void Configure(EntityTypeBuilder<MetaConversionEvent> b)
    {
        b.ToTable("meta_conversion_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64);
        b.Property(x => x.Stage).HasMaxLength(32);
        b.Property(x => x.EventName).HasMaxLength(64);
        b.Property(x => x.EventId).HasMaxLength(96);
        b.Property(x => x.ActionSource).HasMaxLength(32);
        b.Property(x => x.Status).HasMaxLength(16);
        b.Property(x => x.Error).HasMaxLength(500);
        b.Property(x => x.FirstError).HasMaxLength(500);
        b.Property(x => x.MatchKeys).HasMaxLength(120);
        b.Property(x => x.FbTraceId).HasMaxLength(64);
        b.Property(x => x.Trigger).HasMaxLength(32);
        // Una sola fila por lead y etapa (y una aparte para la prueba): es lo que garantiza un evento por
        // etapa, aunque dos procesos lleguen juntos.
        b.HasIndex(x => new { x.LeadId, x.Stage, x.IsTest }).IsUnique();
        b.HasIndex(x => x.CreatedAt);
    }
}
