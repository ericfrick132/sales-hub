using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class GoogleCalendarSettingsConfiguration : IEntityTypeConfiguration<GoogleCalendarSettings>
{
    public void Configure(EntityTypeBuilder<GoogleCalendarSettings> b)
    {
        b.ToTable("google_calendar_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever(); // fila única (Id = 1)
        b.Property(x => x.ClientId).HasMaxLength(256);
        b.Property(x => x.ClientSecret).HasMaxLength(256);
        b.Property(x => x.RefreshToken).HasMaxLength(1024);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.CalendarId).HasMaxLength(256).IsRequired();
        b.Property(x => x.Days).HasMaxLength(32).IsRequired();
        b.Property(x => x.PendingState).HasMaxLength(64);
    }
}
