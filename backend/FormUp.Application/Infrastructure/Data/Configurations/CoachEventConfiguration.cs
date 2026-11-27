using FormUp.Application.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FormUp.Application.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="CoachEvent"/>.
/// </summary>
public class CoachEventConfiguration : IEntityTypeConfiguration<CoachEvent>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CoachEvent> builder)
    {
        // The unique key is what makes a repeated write (a webhook redelivery, a retried request) lose instead of duplicating.
        builder.HasIndex(e => e.IdempotencyKey)
            .IsUnique();

        builder.Property(e => e.IdempotencyKey)
            .HasMaxLength(300);

        builder.Property(e => e.PlanCode)
            .HasMaxLength(50);

        builder.Property(e => e.PreviousPlanCode)
            .HasMaxLength(50);

        builder.Property(e => e.Currency)
            .HasMaxLength(3);

        // Reports read events by type over a time window.
        builder.HasIndex(e => new { e.EventType, e.OccurredAt });

        builder.HasIndex(e => e.CoachUserId);

        builder.HasIndex(e => e.AnonymousCoachKey);

        // Deleting a coach account keeps the event for the funnel totals and drops the link (GDPR).
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(e => e.CoachUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
