using FormUp.Application.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FormUp.Application.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="CoachSubscription"/>.
/// </summary>
public class CoachSubscriptionConfiguration : IEntityTypeConfiguration<CoachSubscription>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CoachSubscription> builder)
    {
        // A professional has at most one subscription.
        builder.HasIndex(cs => cs.ProfessionalProfileId)
            .IsUnique();

        // Webhooks find the coach by the provider's customer or subscription id.
        builder.HasIndex(cs => cs.ExternalCustomerId);

        builder.HasIndex(cs => cs.ExternalSubscriptionId);

        builder.Property(cs => cs.CancelAtPeriodEnd)
            .HasDefaultValue(false);

        builder.HasOne(cs => cs.ProfessionalProfile)
            .WithOne(pp => pp.CoachSubscription)
            .HasForeignKey<CoachSubscription>(cs => cs.ProfessionalProfileId);

        builder.Property(cs => cs.PaysSecondRoleAddOn)
            .HasDefaultValue(false);

        builder.HasOne(cs => cs.SubscriptionPlan)
            .WithMany()
            .HasForeignKey(cs => cs.SubscriptionPlanId);

        builder.HasOne(cs => cs.SubscriptionPlanPrice)
            .WithMany()
            .HasForeignKey(cs => cs.SubscriptionPlanPriceId)
            .OnDelete(DeleteBehavior.NoAction);

        // A promotion is never deleted; if one ever is, the coach's row just loses the reference.
        builder.HasOne(cs => cs.CurrentPromotion)
            .WithMany()
            .HasForeignKey(cs => cs.CurrentPromotionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(cs => cs.PendingSubscriptionPlanPrice)
            .WithMany()
            .HasForeignKey(cs => cs.PendingSubscriptionPlanPriceId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
