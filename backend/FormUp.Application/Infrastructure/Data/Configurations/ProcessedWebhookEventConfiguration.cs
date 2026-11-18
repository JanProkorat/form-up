using FormUp.Application.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FormUp.Application.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="ProcessedWebhookEvent"/>.
/// </summary>
public class ProcessedWebhookEventConfiguration : IEntityTypeConfiguration<ProcessedWebhookEvent>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProcessedWebhookEvent> builder)
    {
        // The unique index is what makes a concurrent duplicate delivery lose the race instead of applying twice.
        builder.HasIndex(e => e.EventId)
            .IsUnique();

        builder.Property(e => e.EventId)
            .HasMaxLength(255);

        builder.Property(e => e.EventType)
            .HasMaxLength(100);
    }
}
