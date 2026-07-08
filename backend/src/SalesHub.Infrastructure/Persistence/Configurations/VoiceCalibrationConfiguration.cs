using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class VoiceCalibrationConfiguration : IEntityTypeConfiguration<VoiceCalibrationTake>
{
    public void Configure(EntityTypeBuilder<VoiceCalibrationTake> b)
    {
        b.ToTable("voice_calibration_takes");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.ScriptKey);
        b.Property(x => x.InstanceName).HasMaxLength(120);
        b.Property(x => x.ScriptKey).HasMaxLength(40);
        b.Property(x => x.ScriptText).HasMaxLength(1000);
        b.Property(x => x.WhatsappMessageId).HasMaxLength(120);
    }
}
