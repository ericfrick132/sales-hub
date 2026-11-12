using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class IgDmSettingsConfiguration : IEntityTypeConfiguration<IgDmSettings>
{
    public void Configure(EntityTypeBuilder<IgDmSettings> b)
    {
        b.ToTable("ig_dm_settings");
        b.HasKey(x => x.ProductKey);
        b.Property(x => x.ProductKey).HasMaxLength(64);
        b.Property(x => x.AccountUsername).HasMaxLength(128);
        b.Property(x => x.Template).IsRequired();

        // Las filas que ya existen siguen mandando por la extensión, como hasta ahora.
        b.Property(x => x.Sender).HasMaxLength(16).IsRequired().HasDefaultValue(IgDmSettings.SenderExtension);
        b.Property(x => x.ColdRamp).HasMaxLength(64);
        b.Property(x => x.FollowerRamp).HasMaxLength(64);
        b.Property(x => x.FollowerTemplate).IsRequired().HasDefaultValue(string.Empty);
        b.Property(x => x.FollowerExclude).HasMaxLength(4000);
        b.Property(x => x.FollowerScanSwipes).HasDefaultValue(IgDmSettings.DefaultFollowerScanSwipes);
        b.HasIndex(x => x.DeviceId);
    }
}

public class IgFollowerConfiguration : IEntityTypeConfiguration<IgFollower>
{
    public void Configure(EntityTypeBuilder<IgFollower> b)
    {
        b.ToTable("ig_followers");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Username).HasMaxLength(128).IsRequired();
        b.Property(x => x.Error).HasMaxLength(300);
        b.Property(x => x.SkippedReason).HasMaxLength(300);
        b.HasIndex(x => new { x.ProductKey, x.Username }).IsUnique();
        b.HasIndex(x => new { x.ProductKey, x.DmSentAt });
    }
}

public class IgDeviceTaskConfiguration : IEntityTypeConfiguration<IgDeviceTask>
{
    public void Configure(EntityTypeBuilder<IgDeviceTask> b)
    {
        b.ToTable("ig_device_tasks");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(24).IsRequired();
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.Handle).HasMaxLength(128);
        b.Property(x => x.Error).HasMaxLength(300);
        b.HasIndex(x => new { x.DeviceId, x.Status });
        b.HasIndex(x => new { x.ProductKey, x.CreatedAt });
    }
}
