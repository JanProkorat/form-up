using FormUp.Application.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FormUp.Application.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="AutomationRun"/>.
/// </summary>
public class AutomationRunConfiguration : IEntityTypeConfiguration<AutomationRun>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AutomationRun> builder)
    {
        builder.ToTable("automation_runs");

        // Prevents sending the same occurrence twice.
        builder.HasIndex(r => new { r.AutomationRuleId, r.ClientProfileId, r.Occurrence }).IsUnique();
        builder.HasIndex(r => new { r.ClientProfileId, r.SentAt });


        // The retry worker only reads rows that wait for a retry.
        builder.HasIndex(r => r.NextRetryAt)
            .HasFilter("next_retry_at IS NOT NULL");

        builder.HasOne(r => r.AutomationRule)
            .WithMany()
            .HasForeignKey(r => r.AutomationRuleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.ClientProfile)
            .WithMany()
            .HasForeignKey(r => r.ClientProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
