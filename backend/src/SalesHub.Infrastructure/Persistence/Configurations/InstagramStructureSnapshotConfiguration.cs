using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public class InstagramStructureSnapshotConfiguration : IEntityTypeConfiguration<InstagramStructureSnapshot>
{
    public void Configure(EntityTypeBuilder<InstagramStructureSnapshot> builder)
    {
        builder.ToTable("instagram_structure_snapshots");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.SnapshotDate)
            .IsRequired();

        builder.Property(x => x.SelectorsJson)
            .HasColumnType("text")
            .IsRequired();

        builder.Property(x => x.RawHtmlJson)
            .HasColumnType("text")
            .IsRequired();

        builder.Property(x => x.LlmModel)
            .HasMaxLength(100);

        builder.Property(x => x.ErrorMessage)
            .HasColumnType("text");

        builder.HasOne(x => x.InstagramAccount)
            .WithMany()
            .HasForeignKey(x => x.InstagramAccountId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => x.SnapshotDate);
        builder.HasIndex(x => x.IsValid);
    }
}
