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
    }
}
