using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Catalog;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps catalogue changes held for the owner's approval.</summary>
public sealed class ProductChangeRequestConfiguration : IEntityTypeConfiguration<ProductChangeRequest>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProductChangeRequest> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("product_change_request", PosDbContext.CatalogSchema);

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(r => r.ChangeType).HasColumnName("change_type").HasMaxLength(64).IsRequired();
        builder.Property(r => r.Summary).HasColumnName("summary")
            .HasMaxLength(ProductChangeRequest.SummaryMaxLength).IsRequired();

        // The held command, replayed verbatim on approval. The 512-character
        // convention for strings does not fit a serialized command, so this one
        // is unbounded.
        builder.Property(r => r.Payload).HasColumnName("payload").HasMaxLength(0).IsRequired();

        builder.Property(r => r.Reason).HasColumnName("reason")
            .HasMaxLength(ProductChangeRequest.ReasonMaxLength).IsRequired();
        builder.Property(r => r.RequestedBy).HasColumnName("requested_by").IsRequired();
        builder.Property(r => r.RequestedAtUtc).HasColumnName("requested_at_utc").IsRequired();
        builder.Property(r => r.Status).HasColumnName("status").HasConversion<int>().IsRequired();
        builder.Property(r => r.DecidedBy).HasColumnName("decided_by");
        builder.Property(r => r.DecidedAtUtc).HasColumnName("decided_at_utc");
        builder.Property(r => r.DecisionNote).HasColumnName("decision_note")
            .HasMaxLength(ProductChangeRequest.ReasonMaxLength);
        builder.Property(r => r.FailureReason).HasColumnName("failure_reason")
            .HasMaxLength(ProductChangeRequest.ReasonMaxLength);

        builder.Ignore(r => r.DomainEvents);
        builder.Ignore(r => r.IsOpen);

        // The queue reads pending-first; a product page reads its own history.
        builder.HasIndex(r => new { r.Status, r.RequestedAtUtc }).HasDatabaseName("ix_product_change_request_status");
        builder.HasIndex(r => r.ProductId).HasDatabaseName("ix_product_change_request_product");
    }
}
