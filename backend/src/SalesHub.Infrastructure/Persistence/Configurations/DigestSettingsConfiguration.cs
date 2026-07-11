using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class DigestSettingsConfiguration : IEntityTypeConfiguration<DigestSettings>
{
    public void Configure(EntityTypeBuilder<DigestSettings> b)
    {
        b.ToTable("digest_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever(); // fila única (Id = 1)
        b.Property(x => x.InstanceName).HasMaxLength(128);
        b.Property(x => x.Phone).HasMaxLength(32);
    }
}
