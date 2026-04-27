using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessage>
{
    public void Configure(EntityTypeBuilder<ConversationMessage> b)
    {
        b.ToTable("conversation_messages");
        b.HasKey(x => x.Id);

        b.Property(x => x.Text).HasColumnType("text").IsRequired();
        b.Property(x => x.WhatsappMessageId).HasMaxLength(128);
        b.Property(x => x.EvolutionInstance).HasMaxLength(128);
        b.Property(x => x.RawJson).HasColumnType("jsonb");

        b.Property(x => x.Direction).HasConversion<int>();
        b.Property(x => x.Status).HasConversion<int>();

        b.HasIndex(x => new { x.LeadId, x.Timestamp });
        b.HasIndex(x => new { x.SellerId, x.IsRead, x.Direction });
        b.HasIndex(x => x.WhatsappMessageId);

        b.HasOne(x => x.Lead)
            .WithMany()
            .HasForeignKey(x => x.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.Seller)
            .WithMany()
            .HasForeignKey(x => x.SellerId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
