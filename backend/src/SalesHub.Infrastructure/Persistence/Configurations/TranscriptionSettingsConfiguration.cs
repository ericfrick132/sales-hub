using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class TranscriptionSettingsConfiguration : IEntityTypeConfiguration<TranscriptionSettings>
{
    public void Configure(EntityTypeBuilder<TranscriptionSettings> b)
    {
        b.ToTable("transcription_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever(); // fila única (Id = 1)
        b.Property(x => x.InstanceName).HasMaxLength(128);
    }
}
