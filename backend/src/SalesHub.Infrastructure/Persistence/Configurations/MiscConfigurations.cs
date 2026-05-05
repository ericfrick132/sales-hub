using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class ApifyRunConfiguration : IEntityTypeConfiguration<ApifyRun>
{
    public void Configure(EntityTypeBuilder<ApifyRun> b)
    {
        b.ToTable("apify_runs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Source).HasConversion<int>();
        b.Property(x => x.ActorId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ProductKey).HasMaxLength(64);
        b.Property(x => x.InputJson).HasColumnType("jsonb");
        b.Property(x => x.ApifyRunId).HasMaxLength(64);
        b.Property(x => x.Status).HasMaxLength(32);
        b.Property(x => x.Error).HasColumnType("text");
        b.HasIndex(x => x.StartedAt);
    }
}

public class MessageOutboxConfiguration : IEntityTypeConfiguration<MessageOutbox>
{
    public void Configure(EntityTypeBuilder<MessageOutbox> b)
    {
        b.ToTable("message_outbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.EvolutionInstance).HasMaxLength(128).IsRequired();
        b.Property(x => x.WhatsappPhone).HasMaxLength(32).IsRequired();
        b.Property(x => x.Message).HasColumnType("text").IsRequired();
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.Error).HasColumnType("text");
        b.HasIndex(x => new { x.Status, x.ScheduledAt });
        b.HasIndex(x => new { x.SellerId, x.Status, x.ScheduledAt });
        b.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Seller).WithMany(s => s.OutboxItems).HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Cascade);
        // Si el item lleva adjunto, lo referenciamos. SetNull para que borrar
        // un MediaAsset no rompa históricos del outbox.
        b.HasOne(x => x.MediaAsset).WithMany().HasForeignKey(x => x.MediaAssetId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class SellerDailyStatsConfiguration : IEntityTypeConfiguration<SellerDailyStats>
{
    public void Configure(EntityTypeBuilder<SellerDailyStats> b)
    {
        b.ToTable("seller_daily_stats");
        b.HasKey(x => new { x.SellerId, x.Date });
        b.HasOne(x => x.Seller).WithMany(s => s.DailyStats).HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Cascade);
    }
}
