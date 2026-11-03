using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class ArcaActivityConfiguration : IEntityTypeConfiguration<ArcaActivity>
{
    public void Configure(EntityTypeBuilder<ArcaActivity> b)
    {
        b.ToTable("arca_activities");
        b.HasKey(x => x.Code);
        b.Property(x => x.Code).HasMaxLength(6);
        b.Property(x => x.Description).HasColumnType("text").IsRequired();
        b.Property(x => x.GroupCode).HasMaxLength(3);
        b.Property(x => x.GroupDescription).HasMaxLength(512);
        b.Property(x => x.DivisionCode).HasMaxLength(2);
        b.Property(x => x.DivisionDescription).HasMaxLength(512);
        b.Property(x => x.Section).HasMaxLength(2);
        b.Property(x => x.SectionDescription).HasMaxLength(512);
        b.HasIndex(x => x.GroupCode);
    }
}
