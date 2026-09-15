using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the append-only sale receipt print log (DATABASE.md §7). Each row records
/// one emission of a sale receipt: the first print at completion and every
/// permissioned reprint afterwards.
/// </summary>
public sealed class SaleReceiptPrintConfiguration : IEntityTypeConfiguration<SaleReceiptPrint>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SaleReceiptPrint> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sale_receipt_print", PosDbContext.SalesSchema);

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(p => p.SaleId).HasColumnName("sale_id").IsRequired();
        builder.Property(p => p.PrintedAtUtc).HasColumnName("printed_at_utc").IsRequired();
        builder.Property(p => p.PrintedByUserId).HasColumnName("printed_by_user_id").IsRequired();
        builder.Property(p => p.IsReprint).HasColumnName("is_reprint").IsRequired();

        builder.Property(p => p.Reason)
            .HasColumnName("reason")
            .HasMaxLength(SaleReceiptPrint.ReasonMaxLength);

        // The log is the per-sale print history, read newest-first for the
        // operator and the audit trail.
        builder.HasIndex(p => new { p.SaleId, p.Id })
            .HasDatabaseName("ix_sale_receipt_print_sale");

        // The reprint window is "any" (POS.md §4): fully online stores answer
        // "what did we reprint today?" from the log, so cover that scan too.
        builder.HasIndex(p => new { p.PrintedAtUtc, p.IsReprint })
            .HasDatabaseName("ix_sale_receipt_print_printed_at");

        // A receipt print always refers to a sale that exists; deletions are not
        // part of either document's lifecycle.
        builder.HasOne<Sale>()
            .WithMany()
            .HasForeignKey(p => p.SaleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}