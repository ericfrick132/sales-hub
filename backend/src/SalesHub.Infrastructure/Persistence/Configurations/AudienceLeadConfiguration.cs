using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class AudienceLeadConfiguration : IEntityTypeConfiguration<AudienceLead>
{
    public void Configure(EntityTypeBuilder<AudienceLead> b)
    {
        b.ToTable("audience_leads");
        b.HasKey(x => x.Id);
        b.Property(x => x.Dataset).HasMaxLength(64);
        b.Property(x => x.Competitor).HasMaxLength(64);
        b.Property(x => x.ProductKey).HasMaxLength(64);
        b.Property(x => x.Segment).HasMaxLength(16);
        b.Property(x => x.ExternalId).HasMaxLength(256);
        b.Property(x => x.Name).HasMaxLength(256);
        b.Property(x => x.Category).HasMaxLength(256);
        b.Property(x => x.Address).HasMaxLength(512);
        b.Property(x => x.City).HasMaxLength(128);
        b.Property(x => x.Province).HasMaxLength(128);
        b.Property(x => x.PostalCode).HasMaxLength(16);
        b.Property(x => x.Country).HasMaxLength(8);
        b.Property(x => x.Phone).HasMaxLength(32);
        b.Property(x => x.PhoneType).HasMaxLength(16);
        b.Property(x => x.PhoneSource).HasMaxLength(64);
        b.Property(x => x.Instagram).HasMaxLength(128);
        b.Property(x => x.Website).HasMaxLength(512);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.PlaceId).HasMaxLength(256);
        b.Property(x => x.RawJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.Dataset, x.ExternalId }).IsUnique();
        b.HasIndex(x => new { x.ProductKey, x.Country, x.Segment });
        b.HasIndex(x => x.Phone);
        b.HasIndex(x => x.Instagram);
    }
}
