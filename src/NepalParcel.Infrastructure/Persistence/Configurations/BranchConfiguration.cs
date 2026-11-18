using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;

namespace NepalParcel.Infrastructure.Persistence.Configurations;

internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("Branches");
        builder.ConfigureAuditableEntity();

        builder.Property(b => b.Code).HasMaxLength(Branch.CodeMaxLength).IsRequired();
        builder.HasIndex(b => b.Code).IsUnique();

        builder.Property(b => b.Name).HasMaxLength(Branch.NameMaxLength).IsRequired();
        builder.Property(b => b.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(b => b.PhoneNumber).HasMaxLength(NepalPhoneNumber.MaxLength).IsRequired();
        builder.Property(b => b.Email).HasMaxLength(Branch.EmailMaxLength);
        builder.Property(b => b.IsActive).IsRequired();

        builder.OwnsNepalAddress(b => b.Address, columnPrefix: "Address");

        builder.Property(b => b.RowVersion).IsRowVersion();

        builder.HasIndex(b => b.Name);
    }
}
