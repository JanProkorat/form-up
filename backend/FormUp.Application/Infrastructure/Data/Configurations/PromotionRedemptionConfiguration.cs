using FormUp.Application.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FormUp.Application.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="PromotionRedemption"/>.
/// </summary>
public class PromotionRedemptionConfiguration : IEntityTypeConfiguration<PromotionRedemption>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PromotionRedemption> builder)
    {
        // One row per coach and promotion: a repeated provider event cannot count a coach twice.
        builder.HasIndex(r => new { r.PromotionId, r.ProfessionalProfileId })
            .IsUnique();

        builder.HasIndex(r => r.ProfessionalProfileId);

        builder.HasOne(r => r.Promotion)
            .WithMany(p => p.Redemptions)
            .HasForeignKey(r => r.PromotionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.ProfessionalProfile)
            .WithMany()
            .HasForeignKey(r => r.ProfessionalProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
