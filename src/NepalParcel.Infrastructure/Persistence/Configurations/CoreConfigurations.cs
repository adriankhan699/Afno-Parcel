using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Warehouses;
using NepalParcel.Infrastructure.Identity;

namespace NepalParcel.Infrastructure.Persistence.Configurations;

internal sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        builder.ToTable("Warehouses");
        builder.ConfigureAuditableEntity();

        builder.Property(w => w.Code).HasMaxLength(Warehouse.CodeMaxLength).IsRequired();
        builder.Property(w => w.Name).HasMaxLength(Warehouse.NameMaxLength).IsRequired();
        builder.Property(w => w.PhoneNumber).HasMaxLength(NepalPhoneNumber.MaxLength).IsRequired();
        builder.Property(w => w.RowVersion).IsRowVersion();
        builder.OwnsNepalAddress(w => w.Address, "Address");

        builder.HasIndex(w => w.Code).IsUnique();
        builder.HasIndex(w => w.BranchId);
        builder.HasOne<Branch>().WithMany().HasForeignKey(w => w.BranchId).IsRequired();
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");
        builder.ConfigureAuditableEntity();

        builder.Property(c => c.FullName).HasMaxLength(Customer.NameMaxLength).IsRequired();
        builder.Property(c => c.CompanyName).HasMaxLength(Customer.NameMaxLength);
        builder.Property(c => c.PhoneNumber).HasMaxLength(NepalPhoneNumber.MaxLength).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(Customer.EmailMaxLength);
        builder.Property(c => c.RowVersion).IsRowVersion();
        builder.Ignore(c => c.IsWalkIn);
        builder.OwnsNepalAddress(c => c.Address, "Address", required: false);

        builder.HasIndex(c => c.PhoneNumber);
        builder.HasIndex(c => c.Email);
        builder.HasIndex(c => c.FullName);

        // One customer profile per login account; many walk-in customers have no account (NULL).
        builder.HasIndex(c => c.UserId).IsUnique().HasFilter("[UserId] IS NOT NULL");
        builder.HasOne<ApplicationUser>().WithOne().HasForeignKey<Customer>(c => c.UserId);
    }
}

internal sealed class CustomerConsentConfiguration : IEntityTypeConfiguration<CustomerConsent>
{
    public void Configure(EntityTypeBuilder<CustomerConsent> builder)
    {
        builder.ToTable("CustomerConsents");
        builder.ConfigureEntity();

        builder.Property(c => c.PolicyVersion).HasMaxLength(20).IsRequired();
        builder.Property(c => c.IpAddress).HasMaxLength(64);

        builder.HasIndex(c => new { c.CustomerId, c.Type });
        builder.HasOne<Customer>().WithMany().HasForeignKey(c => c.CustomerId).IsRequired();
    }
}

internal sealed class ParcelStatusConfiguration : IEntityTypeConfiguration<ParcelStatus>
{
    public void Configure(EntityTypeBuilder<ParcelStatus> builder)
    {
        builder.ToTable("ParcelStatuses");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.Code).HasMaxLength(ParcelStatus.CodeMaxLength).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(80).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(200).IsRequired();
        builder.HasIndex(s => s.Code).IsUnique();

        // Seed data: ids are the ParcelStatusCode enum values and must never change.
        builder.HasData(ParcelStatusCatalog.All.Select(s => new
        {
            Id = (int)s.Code,
            Code = s.Code.ToString(),
            s.Name,
            s.Description,
            SortOrder = (int)s.Code,
            s.IsTerminal,
            IsPublic = true,
        }));
    }
}
