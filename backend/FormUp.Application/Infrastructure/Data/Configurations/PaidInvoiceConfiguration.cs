using FormUp.Application.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FormUp.Application.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="PaidInvoice"/>.
/// </summary>
public class PaidInvoiceConfiguration : IEntityTypeConfiguration<PaidInvoice>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PaidInvoice> builder)
    {
        // One row per provider invoice, however many events announce it.
        builder.HasIndex(pi => pi.ExternalInvoiceId)
            .IsUnique();

        builder.HasIndex(pi => pi.ProfessionalProfileId);

        builder.HasIndex(pi => pi.PaidAt);

        // Account deletion is a hard delete; the invoice stays in revenue totals with no link to the person.
        builder.HasOne(pi => pi.ProfessionalProfile)
            .WithMany()
            .HasForeignKey(pi => pi.ProfessionalProfileId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
