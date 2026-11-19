using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Parcels;
using NepalParcel.Infrastructure.Identity;

namespace NepalParcel.Infrastructure.Persistence.Configurations;

internal sealed class DeliveryAgentConfiguration : IEntityTypeConfiguration<DeliveryAgent>
{
    public void Configure(EntityTypeBuilder<DeliveryAgent> builder)
    {
        builder.ToTable("DeliveryAgents");
        builder.ConfigureAuditableEntity();

        builder.Property(a => a.FullName).HasMaxLength(DeliveryAgent.NameMaxLength).IsRequired();
        builder.Property(a => a.PhoneNumber).HasMaxLength(NepalPhoneNumber.MaxLength).IsRequired();
        builder.Property(a => a.VehicleType).HasMaxLength(40);
        builder.Property(a => a.VehicleNumber).HasMaxLength(30);
        builder.Property(a => a.LicenseNumber).HasMaxLength(40);
        builder.Property(a => a.RowVersion).IsRowVersion();

        builder.HasIndex(a => a.UserId).IsUnique();
        builder.HasIndex(a => new { a.BranchId, a.IsActive, a.IsAvailable });

        builder.HasOne<ApplicationUser>().WithOne().HasForeignKey<DeliveryAgent>(a => a.UserId).IsRequired();
        builder.HasOne<Branch>().WithMany().HasForeignKey(a => a.BranchId).IsRequired();
    }
}

internal sealed class DeliveryAssignmentConfiguration : IEntityTypeConfiguration<DeliveryAssignment>
{
    public void Configure(EntityTypeBuilder<DeliveryAssignment> builder)
    {
        builder.ToTable("DeliveryAssignments", t => t.HasCheckConstraint("CK_DeliveryAssignments_Attempt", "[AttemptNumber] >= 1"));
        builder.ConfigureAuditableEntity();

        builder.Property(a => a.FailureReason).HasMaxLength(300);
        builder.Property(a => a.RowVersion).IsRowVersion();
        builder.Ignore(a => a.IsOpen);

        builder.HasIndex(a => new { a.DeliveryAgentId, a.Status });
        builder.HasIndex(a => new { a.DeliveryAgentId, a.AssignedAtUtc });
        builder.HasIndex(a => a.ParcelId);

        // A parcel can have only one open assignment per type at a time (prevents double assignment).
        builder.HasIndex(a => new { a.ParcelId, a.Type })
            .IsUnique()
            .HasFilter("[Status] IN ('Assigned','Accepted','InProgress')");

        builder.HasOne<Parcel>().WithMany().HasForeignKey(a => a.ParcelId).IsRequired();
        builder.HasOne<DeliveryAgent>().WithMany().HasForeignKey(a => a.DeliveryAgentId).IsRequired();
    }
}

internal sealed class DeliveryProofConfiguration : IEntityTypeConfiguration<DeliveryProof>
{
    public void Configure(EntityTypeBuilder<DeliveryProof> builder)
    {
        builder.ToTable("DeliveryProofs");
        builder.ConfigureEntity();

        builder.Property(p => p.StorageKey).HasMaxLength(300);
        builder.Property(p => p.RecipientName).HasMaxLength(150);

        builder.HasIndex(p => p.ParcelId);
        builder.HasIndex(p => p.AssignmentId);

        builder.HasOne<Parcel>().WithMany().HasForeignKey(p => p.ParcelId).IsRequired();
        builder.HasOne<DeliveryAssignment>().WithMany().HasForeignKey(p => p.AssignmentId);
        builder.HasOne<DeliveryAgent>().WithMany().HasForeignKey(p => p.CapturedByAgentId);
    }
}

internal sealed class DeliveryOtpConfiguration : IEntityTypeConfiguration<DeliveryOtp>
{
    public void Configure(EntityTypeBuilder<DeliveryOtp> builder)
    {
        builder.ToTable("DeliveryOtps", t => t.HasCheckConstraint("CK_DeliveryOtps_Attempts", "[FailedAttempts] >= 0"));
        builder.ConfigureEntity();

        // Only a keyed hash of the code is stored, never the code.
        builder.Property(o => o.CodeHash).HasMaxLength(128).IsRequired();
        builder.Property(o => o.RowVersion).IsRowVersion();

        builder.HasIndex(o => new { o.AssignmentId, o.CreatedAtUtc });
        builder.HasOne<Parcel>().WithMany().HasForeignKey(o => o.ParcelId).IsRequired();
        builder.HasOne<DeliveryAssignment>().WithMany().HasForeignKey(o => o.AssignmentId).IsRequired();
    }
}
