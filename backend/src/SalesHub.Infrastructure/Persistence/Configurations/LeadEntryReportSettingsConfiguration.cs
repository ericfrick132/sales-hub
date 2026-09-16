using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class LeadEntryReportSettingsConfiguration : IEntityTypeConfiguration<LeadEntryReportSettings>
{
    public void Configure(EntityTypeBuilder<LeadEntryReportSettings> b)
    {
        b.ToTable("lead_entry_report_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever(); // fila única (Id = 1)
        b.Property(x => x.RecipientPhone).HasMaxLength(32).IsRequired();
        b.Property(x => x.SendInstanceName).HasMaxLength(128);
        b.Property(x => x.LastError).HasMaxLength(500);
    }
}
