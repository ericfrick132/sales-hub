using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class OutboundContactConfiguration : IEntityTypeConfiguration<OutboundContact>
{
    public void Configure(EntityTypeBuilder<OutboundContact> b)
    {
        b.ToTable("outbound_contacts");
        b.HasKey(x => x.Id);
        b.Property(x => x.InstanceName).HasMaxLength(128).IsRequired();
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.Message).HasColumnType("text").IsRequired();
        b.Property(x => x.Error).HasMaxLength(512);
        b.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.InstanceName);
        // Nunca dos aperturas al mismo lead (ni desde dos líneas outbound).
        b.HasIndex(x => x.LeadId).IsUnique();
    }
}
