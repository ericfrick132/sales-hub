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
        b.Property(x => x.ConnectedPhoneNumber).HasMaxLength(32);
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.LastQrCodeBase64).HasColumnType("text");
    }
}
