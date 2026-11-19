using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Warehouses;
using NepalParcel.Infrastructure.Identity;

namespace NepalParcel.Infrastructure.Persistence.Configurations;

internal sealed class ParcelMovementConfiguration : IEntityTypeConfiguration<ParcelMovement>
{
    public void Configure(EntityTypeBuilder<ParcelMovement> builder)
    {
        builder.ToTable("ParcelMovements");
        builder.ConfigureEntity();

        builder.Property(m => m.StaffName).HasMaxLength(150);
        builder.Property(m => m.Remarks).HasMaxLength(500);

        // One parcel's movements in order, and one warehouse's activity.
        builder.HasIndex(m => new { m.ParcelId, m.OccurredAtUtc });
        builder.HasIndex(m => new { m.WarehouseId, m.OccurredAtUtc });
        builder.HasIndex(m => m.ToWarehouseId);

        builder.HasOne<Parcel>().WithMany().HasForeignKey(m => m.ParcelId).IsRequired();
        builder.HasOne<Warehouse>().WithMany().HasForeignKey(m => m.WarehouseId).IsRequired();
        builder.HasOne<Warehouse>().WithMany().HasForeignKey(m => m.ToWarehouseId);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(m => m.StaffUserId).IsRequired();
    }
}

internal sealed class ParcelScanConfiguration : IEntityTypeConfiguration<ParcelScan>
{
    public void Configure(EntityTypeBuilder<ParcelScan> builder)
    {
        builder.ToTable("ParcelScans");
        builder.ConfigureEntity();

        builder.Property(s => s.StaffName).HasMaxLength(150);
        builder.Property(s => s.Remarks).HasMaxLength(500);

        builder.HasIndex(s => new { s.WarehouseId, s.ScannedAtUtc });
        builder.HasIndex(s => new { s.ParcelId, s.ScannedAtUtc });
        builder.HasIndex(s => new { s.StaffUserId, s.ScannedAtUtc });
        builder.HasIndex(s => s.MovementId);

        builder.HasOne<Parcel>().WithMany().HasForeignKey(s => s.ParcelId).IsRequired();
        builder.HasOne<Warehouse>().WithMany().HasForeignKey(s => s.WarehouseId).IsRequired();
        builder.HasOne<ParcelMovement>().WithMany().HasForeignKey(s => s.MovementId).IsRequired();
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.StaffUserId).IsRequired();
    }
}

internal sealed class StaffAssignmentConfiguration : IEntityTypeConfiguration<StaffAssignment>
{
    public void Configure(EntityTypeBuilder<StaffAssignment> builder)
    {
        builder.ToTable("StaffAssignments");
        builder.ConfigureEntity();

        // A person works in one place at a time; ended assignments stay as history.
        builder.HasIndex(a => a.UserId).IsUnique().HasFilter("[EndedAtUtc] IS NULL");
        builder.HasIndex(a => new { a.BranchId, a.EndedAtUtc });
        builder.HasIndex(a => a.WarehouseId);
        builder.Ignore(a => a.IsActive);

        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.UserId).IsRequired();
        builder.HasOne<Branch>().WithMany().HasForeignKey(a => a.BranchId).IsRequired();
        builder.HasOne<Warehouse>().WithMany().HasForeignKey(a => a.WarehouseId);
    }
}
