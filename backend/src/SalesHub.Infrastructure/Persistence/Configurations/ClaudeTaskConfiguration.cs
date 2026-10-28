using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class ClaudeTaskConfiguration : IEntityTypeConfiguration<ClaudeTask>
{
    public void Configure(EntityTypeBuilder<ClaudeTask> b)
    {
        b.ToTable("claude_tasks");
        b.HasKey(x => x.Id);
        b.Property(x => x.InstanceName).HasMaxLength(128);
        b.Property(x => x.ReplyTo).HasMaxLength(32);
        b.Property(x => x.Prompt).HasColumnType("text");
        b.Property(x => x.ResumeSessionId).HasMaxLength(64);
        b.Property(x => x.Status).HasMaxLength(16);
        b.Property(x => x.SessionId).HasMaxLength(64);
        b.Property(x => x.Result).HasColumnType("text");
        b.Property(x => x.CostUsd).HasPrecision(10, 4);
        b.Property(x => x.Error).HasMaxLength(1000);
        b.Property(x => x.Code).HasMaxLength(8);
        b.Property(x => x.AudioMessageId).HasMaxLength(128);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => new { x.ReplyTo, x.Code });
        b.HasIndex(x => x.AudioMessageId);
    }
}
