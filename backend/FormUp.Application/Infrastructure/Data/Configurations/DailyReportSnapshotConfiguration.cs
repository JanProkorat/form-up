using FormUp.Application.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FormUp.Application.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="DailyReportSnapshot"/>.
/// </summary>
public class DailyReportSnapshotConfiguration : IEntityTypeConfiguration<DailyReportSnapshot>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DailyReportSnapshot> builder)
    {
        // One row per UTC day; a concurrent second writer loses on this index and updates instead.
        builder.HasIndex(s => s.Day)
            .IsUnique();

        builder.Property(s => s.MedianClientsPerCoach)
            .HasPrecision(10, 2);

        builder.OwnsMany(s => s.PayingCoachesByPlan, owned => owned.ToJson());
        builder.OwnsMany(s => s.MonthlyRevenueFull, owned => owned.ToJson());
        builder.OwnsMany(s => s.MonthlyRevenueAfterPromotions, owned => owned.ToJson());
    }
}
