using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Warehouses;

namespace NepalParcel.Infrastructure.Persistence.Configurations;

internal sealed class ParcelConfiguration : IEntityTypeConfiguration<Parcel>
{
    public void Configure(EntityTypeBuilder<Parcel> builder)
    {
        builder.ToTable("Parcels", t =>
        {
            t.HasCheckConstraint("CK_Parcels_WeightKg", "[WeightKg] > 0");
            t.HasCheckConstraint("CK_Parcels_Amounts", "[DeclaredValue] >= 0 AND [DeliveryCharge] >= 0 AND [CodAmount] >= 0");
        });
        builder.ConfigureAuditableEntity();

        builder.Property(p => p.TrackingNumber).HasMaxLength(TrackingNumbers.Length).IsRequired();
        builder.Property(p => p.ReceiverName).HasMaxLength(Parcel.NameMaxLength).IsRequired();
        builder.Property(p => p.ReceiverPhone).HasMaxLength(20).IsRequired();
        builder.Property(p => p.WeightKg).HasPrecision(10, 3);
        builder.Property(p => p.Notes).HasMaxLength(Parcel.NotesMaxLength);
        builder.Property(p => p.RowVersion).IsRowVersion();
        builder.Ignore(p => p.Status);
        builder.Ignore(p => p.IsCod);
        builder.OwnsNepalAddress(p => p.ReceiverAddress, "ReceiverAddress");

        // Every tracking number is globally unique.
        builder.HasIndex(p => p.TrackingNumber).IsUnique();
        builder.HasIndex(p => p.SenderCustomerId);
        builder.HasIndex(p => new { p.CurrentStatusId, p.BookedAtUtc });
        builder.HasIndex(p => p.OriginBranchId);
        builder.HasIndex(p => p.DestinationBranchId);
        builder.HasIndex(p => p.CurrentBranchId);
        builder.HasIndex(p => p.CurrentWarehouseId);
        builder.HasIndex(p => p.ReceiverPhone);

        builder.HasOne<Customer>().WithMany().HasForeignKey(p => p.SenderCustomerId).IsRequired();
        builder.HasOne<Branch>().WithMany().HasForeignKey(p => p.OriginBranchId).IsRequired();
        builder.HasOne<Branch>().WithMany().HasForeignKey(p => p.DestinationBranchId);
        builder.HasOne<Branch>().WithMany().HasForeignKey(p => p.CurrentBranchId);
        builder.HasOne<Warehouse>().WithMany().HasForeignKey(p => p.CurrentWarehouseId);
        builder.HasOne<ParcelStatus>().WithMany().HasForeignKey(p => p.CurrentStatusId).IsRequired();

        builder.HasMany(p => p.Items).WithOne().HasForeignKey(i => i.ParcelId).IsRequired();
        builder.HasMany(p => p.StatusHistory).WithOne().HasForeignKey(h => h.ParcelId).IsRequired();
        builder.Navigation(p => p.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(p => p.StatusHistory).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ParcelItemConfiguration : IEntityTypeConfiguration<ParcelItem>
{
    public void Configure(EntityTypeBuilder<ParcelItem> builder)
    {
        builder.ToTable("ParcelItems", t =>
        {
            t.HasCheckConstraint("CK_ParcelItems_Quantity", "[Quantity] > 0");
            t.HasCheckConstraint("CK_ParcelItems_WeightKg", "[WeightKg] > 0");
        });
        builder.ConfigureEntity();

        builder.Property(i => i.Description).HasMaxLength(ParcelItem.DescriptionMaxLength).IsRequired();
        builder.Property(i => i.Category).HasMaxLength(ParcelItem.CategoryMaxLength);
        builder.Property(i => i.WeightKg).HasPrecision(10, 3);

        builder.HasIndex(i => i.ParcelId);
    }
}

internal sealed class ParcelStatusHistoryConfiguration : IEntityTypeConfiguration<ParcelStatusHistory>
{
    public void Configure(EntityTypeBuilder<ParcelStatusHistory> builder)
    {
        builder.ToTable("ParcelStatusHistory");
        builder.ConfigureEntity();

        builder.Property(h => h.ChangedByName).HasMaxLength(150);
        builder.Property(h => h.Remarks).HasMaxLength(500);

        // The timeline of one parcel, in order.
        builder.HasIndex(h => new { h.ParcelId, h.OccurredAtUtc });
        builder.HasIndex(h => new { h.ToStatusId, h.OccurredAtUtc });
        builder.HasIndex(h => h.BranchId);
        builder.HasIndex(h => h.WarehouseId);

        builder.HasOne<ParcelStatus>().WithMany().HasForeignKey(h => h.ToStatusId).IsRequired();
        builder.HasOne<ParcelStatus>().WithMany().HasForeignKey(h => h.FromStatusId);
        builder.HasOne<Branch>().WithMany().HasForeignKey(h => h.BranchId);
        builder.HasOne<Warehouse>().WithMany().HasForeignKey(h => h.WarehouseId);
    }
}
