using FormUp.Application.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FormUp.Application.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="SubscriptionPlanPrice"/>.
/// </summary>
public class SubscriptionPlanPriceConfiguration : IEntityTypeConfiguration<SubscriptionPlanPrice>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SubscriptionPlanPrice> builder)
    {
        builder.Property(spp => spp.Currency)
            .HasMaxLength(3);

        builder.Property(spp => spp.ExternalPriceId)
            .HasMaxLength(200);

        builder.HasOne(spp => spp.SubscriptionPlan)
            .WithMany(sp => sp.Prices)
            .HasForeignKey(spp => spp.SubscriptionPlanId);

        // A plan has at most one open price row.
        builder.HasIndex(spp => spp.SubscriptionPlanId)
            .IsUnique()
            .HasFilter("valid_to IS NULL");
    }
}
