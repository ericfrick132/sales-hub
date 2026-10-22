using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class BotProposalConfiguration : IEntityTypeConfiguration<BotProposal>
{
    public void Configure(EntityTypeBuilder<BotProposal> b)
    {
        b.ToTable("bot_proposals");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductKey).HasMaxLength(64);
        b.Property(x => x.Kind).HasMaxLength(16);
        b.Property(x => x.Target).HasMaxLength(128);
        b.Property(x => x.Title).HasMaxLength(256);
        b.Property(x => x.Rationale).HasColumnType("text");
        b.Property(x => x.OldValue).HasColumnType("text");
        b.Property(x => x.NewValue).HasColumnType("text");
        b.Property(x => x.ExamplesJson).HasColumnType("jsonb");
        b.Property(x => x.Status).HasMaxLength(16);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => x.Day);
    }
}

public class BotLearningRunConfiguration : IEntityTypeConfiguration<BotLearningRun>
{
    public void Configure(EntityTypeBuilder<BotLearningRun> b)
    {
        b.ToTable("bot_learning_runs");
        b.HasKey(x => x.Day);
        b.Property(x => x.Error).HasMaxLength(1000);
    }
}
