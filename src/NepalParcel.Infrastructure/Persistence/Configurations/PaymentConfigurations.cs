using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments", t => t.HasCheckConstraint("CK_Payments_Amount", "[Amount] > 0"));
        builder.ConfigureAuditableEntity();

        builder.Property(p => p.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(p => p.Reference).HasMaxLength(100);
        builder.Property(p => p.RowVersion).IsRowVersion();
        builder.Ignore(p => p.ReceiptNumber);

        builder.HasIndex(p => p.ParcelId);
        builder.HasIndex(p => new { p.Status, p.Method });
        builder.HasIndex(p => p.PayerCustomerId);

        builder.HasOne<Parcel>().WithMany().HasForeignKey(p => p.ParcelId).IsRequired();
        builder.HasOne<Customer>().WithMany().HasForeignKey(p => p.PayerCustomerId);
    }
}

internal sealed class PaymentTransactionConfiguration : IEntityTypeConfiguration<PaymentTransaction>
{
    public void Configure(EntityTypeBuilder<PaymentTransaction> builder)
    {
        builder.ToTable("PaymentTransactions", t => t.HasCheckConstraint("CK_PaymentTransactions_Amount", "[Amount] > 0"));
        builder.ConfigureEntity();

        builder.Property(t => t.Provider).HasMaxLength(50).IsRequired();
        builder.Property(t => t.ProviderReference).HasMaxLength(100);
        builder.Property(t => t.FailureReason).HasMaxLength(500);
        builder.Property(t => t.MerchantReference).HasMaxLength(40).IsRequired();
        builder.Property(t => t.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        builder.HasIndex(t => t.PaymentId);
        builder.HasIndex(t => new { t.Status, t.CreatedAtUtc });

        // Our own reference identifies exactly one attempt: a callback can only ever refer to one transaction.
        builder.HasIndex(t => t.MerchantReference).IsUnique();

        // The provider's transaction id can appear only once per provider (idempotency).
        builder.HasIndex(t => new { t.Provider, t.ProviderReference })
            .IsUnique()
            .HasFilter("[ProviderReference] IS NOT NULL");

        builder.HasOne<Payment>().WithMany().HasForeignKey(t => t.PaymentId).IsRequired();
    }
}

internal sealed class CodCollectionConfiguration : IEntityTypeConfiguration<CodCollection>
{
    public void Configure(EntityTypeBuilder<CodCollection> builder)
    {
        builder.ToTable("CODCollections", t => t.HasCheckConstraint("CK_CODCollections_Amounts", "[ExpectedAmount] >= 0 AND [CollectedAmount] >= 0"));
        builder.ConfigureAuditableEntity();

        builder.Property(c => c.RowVersion).IsRowVersion();
        builder.Ignore(c => c.Variance);

        // Cash for a parcel can be collected only once.
        builder.HasIndex(c => c.ParcelId).IsUnique();
        builder.HasIndex(c => new { c.DeliveryAgentId, c.Status });
        builder.HasIndex(c => c.SettlementId);

        builder.HasOne<Parcel>().WithMany().HasForeignKey(c => c.ParcelId).IsRequired();
        builder.HasOne<DeliveryAgent>().WithMany().HasForeignKey(c => c.DeliveryAgentId).IsRequired();
        builder.HasOne<CodSettlement>().WithMany().HasForeignKey(c => c.SettlementId);
    }
}

internal sealed class CodSettlementConfiguration : IEntityTypeConfiguration<CodSettlement>
{
    public void Configure(EntityTypeBuilder<CodSettlement> builder)
    {
        builder.ToTable("CODSettlements", t => t.HasCheckConstraint("CK_CODSettlements_Total", "[TotalAmount] >= 0 AND [CollectionCount] >= 1"));
        builder.ConfigureAuditableEntity();

        builder.Property(s => s.ReferenceNumber).HasMaxLength(30).IsRequired();
        builder.Property(s => s.Remarks).HasMaxLength(500);
        builder.Property(s => s.RowVersion).IsRowVersion();
        builder.Ignore(s => s.Variance);

        builder.HasIndex(s => s.ReferenceNumber).IsUnique();
        builder.HasIndex(s => new { s.BranchId, s.Status });
        builder.HasIndex(s => s.DeliveryAgentId);

        builder.HasOne<Branch>().WithMany().HasForeignKey(s => s.BranchId).IsRequired();
        builder.HasOne<DeliveryAgent>().WithMany().HasForeignKey(s => s.DeliveryAgentId);
    }
}

internal sealed class TestGatewaySessionConfiguration : IEntityTypeConfiguration<TestGatewaySession>
{
    public void Configure(EntityTypeBuilder<TestGatewaySession> builder)
    {
        builder.ToTable("TestGatewaySessions");
        builder.ConfigureEntity();

        builder.Property(s => s.Provider).HasMaxLength(50).IsRequired();
        builder.Property(s => s.MerchantReference).HasMaxLength(40).IsRequired();
        builder.Property(s => s.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(s => s.ProviderTransactionId).HasMaxLength(100);

        builder.HasIndex(s => s.MerchantReference).IsUnique();
        builder.HasIndex(s => s.ProviderTransactionId).IsUnique().HasFilter("[ProviderTransactionId] IS NOT NULL");
    }
}
