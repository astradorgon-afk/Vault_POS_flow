using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Quarantine;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps quarantine incidents. The aggregate carries three immutable collections
/// (lines, photographs, timeline); each is a child table so head office can query
/// them without deserialising a whole incident.
/// </summary>
public sealed class QuarantineIncidentConfiguration : IEntityTypeConfiguration<QuarantineIncident>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<QuarantineIncident> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("quarantine_incident", PosDbContext.QuarantineSchema);

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(i => i.Number).HasColumnName("number").HasMaxLength(20).IsRequired();
        builder.Property(i => i.Status).HasColumnName("status").HasConversion<short>().IsRequired();
        builder.Property(i => i.LocationId).HasColumnName("location_id").IsRequired();
        builder.Property(i => i.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(i => i.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.Property(i => i.ResolvedByUserId).HasColumnName("resolved_by_user_id");
        builder.Property(i => i.ResolvedAtUtc).HasColumnName("resolved_at_utc");
        builder.Property(i => i.InvestigatedAtUtc).HasColumnName("investigated_at_utc");
        builder.Property(i => i.Note).HasColumnName("note").HasMaxLength(512);

        builder.HasIndex(i => i.Number)
            .IsUnique()
            .HasDatabaseName("ux_quarantine_incident_number");

        builder.HasIndex(i => new { i.LocationId, i.CreatedAtUtc })
            .HasDatabaseName("ix_quarantine_incident_location_time");

        builder.HasIndex(i => new { i.Status, i.CreatedAtUtc })
            .HasDatabaseName("ix_quarantine_incident_status_time");

        builder.HasMany(i => i.Lines)
            .WithOne()
            .HasForeignKey(l => l.IncidentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(i => i.Photos)
            .WithOne()
            .HasForeignKey(p => p.IncidentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(i => i.Timeline)
            .WithOne()
            .HasForeignKey(e => e.IncidentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps one quarantined line of an incident.</summary>
public sealed class QuarantineIncidentLineConfiguration : IEntityTypeConfiguration<QuarantineIncidentLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<QuarantineIncidentLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("quarantine_incident_line", PosDbContext.QuarantineSchema);

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(l => l.IncidentId).HasColumnName("incident_id").IsRequired();
        builder.Property(l => l.LineNo).HasColumnName("line_no").IsRequired();

        builder.Property(l => l.Barcode)
            .HasColumnName("barcode")
            .HasMaxLength(Barcode.MaxLength)
            .IsRequired();

        builder.Property(l => l.Quantity)
            .HasColumnName("quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.Property(l => l.UnitCost)
            .HasColumnName("unit_cost")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(l => l.ClaimedProductName).HasColumnName("claimed_product_name").HasMaxLength(512);

        builder.Property(l => l.ProductId).HasColumnName("product_id");
        builder.Property(l => l.BatchId).HasColumnName("batch_id");

        builder.Property(l => l.DispositionedQuantity)
            .HasColumnName("dispositioned_quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.Property(l => l.Disposition).HasColumnName("disposition").HasConversion<short>().IsRequired();
        builder.Property(l => l.DispositionedByUserId).HasColumnName("dispositioned_by_user_id");
        builder.Property(l => l.DispositionedAtUtc).HasColumnName("dispositioned_at_utc");
        builder.Property(l => l.DispositionNote).HasColumnName("disposition_note").HasMaxLength(512);

        builder.Ignore(l => l.RemainingQuantity);

        builder.HasIndex(l => new { l.IncidentId, l.LineNo })
            .IsUnique()
            .HasDatabaseName("ux_quarantine_incident_line_position");
    }
}

/// <summary>Maps a photograph attached to an incident.</summary>
public sealed class QuarantinePhotoConfiguration : IEntityTypeConfiguration<QuarantinePhoto>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<QuarantinePhoto> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("quarantine_photo", PosDbContext.QuarantineSchema);

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(p => p.IncidentId).HasColumnName("incident_id").IsRequired();
        builder.Property(p => p.FileName).HasColumnName("file_name").HasMaxLength(512).IsRequired();
        builder.Property(p => p.ContentType).HasColumnName("content_type").HasMaxLength(128).IsRequired();
        builder.Property(p => p.Data).HasColumnName("data").IsRequired();
        builder.Property(p => p.Note).HasColumnName("note").HasMaxLength(512);

        builder.Property(p => p.UploadedByUserId).HasColumnName("uploaded_by_user_id").IsRequired();
        builder.Property(p => p.UploadedAtUtc).HasColumnName("uploaded_at_utc").IsRequired();

        builder.HasIndex(p => new { p.IncidentId, p.UploadedAtUtc })
            .HasDatabaseName("ix_quarantine_photo_incident_time");
    }
}

/// <summary>Maps one step of an incident's audit timeline.</summary>
public sealed class QuarantineEventConfiguration : IEntityTypeConfiguration<QuarantineEvent>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<QuarantineEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("quarantine_event", PosDbContext.QuarantineSchema);

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.IncidentId).HasColumnName("incident_id").IsRequired();
        builder.Property(e => e.Sequence).HasColumnName("sequence").IsRequired();
        builder.Property(e => e.Kind).HasColumnName("kind").HasConversion<short>().IsRequired();
        builder.Property(e => e.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(e => e.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();

        builder.Property(e => e.Quantity)
            .HasColumnName("quantity")
            .HasPrecision(18, Quantity.Scale);

        builder.Property(e => e.Note).HasColumnName("note").HasMaxLength(512);

        builder.HasIndex(e => new { e.IncidentId, e.Sequence })
            .IsUnique()
            .HasDatabaseName("ux_quarantine_incident_event_sequence");
    }
}