using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class TranscriptionPhoneConfiguration : IEntityTypeConfiguration<TranscriptionPhone>
{
    public void Configure(EntityTypeBuilder<TranscriptionPhone> b)
    {
        b.ToTable("transcription_phones");
        b.HasKey(x => x.Id);
        b.Property(x => x.Phone).IsRequired().HasMaxLength(32);
        b.Property(x => x.Label).HasMaxLength(80);
        b.HasIndex(x => x.Phone).IsUnique();
    }
}
