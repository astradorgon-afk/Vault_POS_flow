using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Transfers;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps pre-approval tokens and their product scope rows. A token's scope is a
/// route (a store pair), an optional product subset, an optional value ceiling
/// and a validity window; the product subset is a child table so it can be
/// queried without deserialising a collection.
/// </summary>
public sealed class PreApprovalTokenConfiguration : IEntityTypeConfiguration<PreApprovalToken>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PreApprovalToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("pre_approval_token", PosDbContext.TransfersSchema);

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(t => t.Number).HasColumnName("number").HasMaxLength(20).IsRequired();
        builder.Property(t => t.SourceLocationId).HasColumnName("source_location_id").IsRequired();
        builder.Property(t => t.DestinationLocationId).HasColumnName("destination_location_id").IsRequired();

        builder.Property(t => t.MaxValue)
            .HasColumnName("max_value")
            .HasPrecision(19, Money.StorageScale);

        builder.Property(t => t.ValidFromUtc).HasColumnName("valid_from_utc").IsRequired();
        builder.Property(t => t.ValidUntilUtc).HasColumnName("valid_until_utc").IsRequired();
        builder.Property(t => t.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(t => t.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(t => t.Status).HasColumnName("status").HasConversion<short>().IsRequired();

        builder.Property(t => t.ConsumedByTransferId).HasColumnName("consumed_by_transfer_id");
        builder.Property(t => t.ConsumedAtUtc).HasColumnName("consumed_at_utc");
        builder.Property(t => t.RevokedByUserId).HasColumnName("revoked_by_user_id");
        builder.Property(t => t.RevokedAtUtc).HasColumnName("revoked_at_utc");
        builder.Property(t => t.RevokeReason).HasColumnName("revoke_reason").HasMaxLength(512);

        builder.HasIndex(t => t.Number)
            .IsUnique()
            .HasDatabaseName("ux_pre_approval_token_number");

        builder.HasIndex(t => new { t.SourceLocationId, t.CreatedAtUtc })
            .HasDatabaseName("ix_pre_approval_token_source_time");

        builder.HasMany(t => t.ProductRows)
            .WithOne()
            .HasForeignKey(p => p.TokenId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(t => t.Products);
    }
}

/// <summary>Maps one product in a pre-approval token's scope.</summary>
public sealed class PreApprovalTokenProductConfiguration : IEntityTypeConfiguration<PreApprovalTokenProduct>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PreApprovalTokenProduct> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("pre_approval_token_product", PosDbContext.TransfersSchema);

        builder.HasKey(p => new { p.TokenId, p.Ordinal });
        builder.Property(p => p.TokenId).HasColumnName("token_id").IsRequired();
        builder.Property(p => p.Ordinal).HasColumnName("ordinal").IsRequired();
        builder.Property(p => p.ProductId).HasColumnName("product_id").IsRequired();
    }
}