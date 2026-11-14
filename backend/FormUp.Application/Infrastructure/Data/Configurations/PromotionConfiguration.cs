using FormUp.Application.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FormUp.Application.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="Promotion"/>.
/// </summary>
public class PromotionConfiguration : IEntityTypeConfiguration<Promotion>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Promotion> builder)
    {
        builder.Property(p => p.Name).HasMaxLength(200);
        builder.Property(p => p.Currency).HasMaxLength(3);
        builder.Property(p => p.Code).HasMaxLength(50);
        builder.Property(p => p.CodeNormalized).HasMaxLength(50);
        builder.Property(p => p.StripeCouponId).HasMaxLength(255);
        builder.Property(p => p.StripePromotionCodeId).HasMaxLength(255);

        // Lookup by typed code; uniqueness among not-ended promotions depends on derived status, so it is checked in code.
        builder.HasIndex(p => p.CodeNormalized);

        // The webhook maps a subscription's discount back to the promotion by these ids.
        builder.HasIndex(p => p.StripeCouponId);
        builder.HasIndex(p => p.StripePromotionCodeId);
    }
}
