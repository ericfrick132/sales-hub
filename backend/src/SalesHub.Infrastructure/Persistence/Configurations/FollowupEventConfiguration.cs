using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class FollowupEventConfiguration : IEntityTypeConfiguration<FollowupEvent>
{
    public void Configure(EntityTypeBuilder<FollowupEvent> b)
    {
        b.ToTable("followup_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Trigger).HasMaxLength(64);
        b.Property(x => x.ExternalRef).HasMaxLength(128);
        b.Property(x => x.EventType).HasMaxLength(32).IsRequired();
        b.Property(x => x.Channel).HasMaxLength(16);
        b.HasIndex(x => new { x.ProductKey, x.CreatedAt });
        b.HasIndex(x => new { x.ProductKey, x.EventType });
    }
}
