using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Integrations;
using NepalParcel.Domain.Notifications;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Pricing;

namespace NepalParcel.Infrastructure.Persistence.Configurations;

internal sealed class NotificationOutboxConfiguration : IEntityTypeConfiguration<NotificationOutboxMessage>
{
    public void Configure(EntityTypeBuilder<NotificationOutboxMessage> builder)
    {
        builder.ToTable("NotificationOutbox");
        builder.ConfigureEntity();

        builder.Property(m => m.Recipient).HasMaxLength(256).IsRequired();
        builder.Property(m => m.TemplateKey).HasMaxLength(100).IsRequired();
        builder.Property(m => m.PayloadJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(m => m.Language).HasMaxLength(10).IsRequired();
        builder.Property(m => m.DedupeKey).HasMaxLength(100);
        builder.Property(m => m.LastError).HasMaxLength(1000);
        builder.Property(m => m.SkipReason).HasMaxLength(300);
        builder.Property(m => m.Provider).HasMaxLength(50);
        builder.Property(m => m.ProviderMessageId).HasMaxLength(100);
        builder.Property(m => m.RenderedBody).HasMaxLength(1000);
        builder.Property(m => m.DeliveryError).HasMaxLength(500);
        builder.Property(m => m.RowVersion).IsRowVersion();

        // The worker polls for due messages.
        builder.HasIndex(m => new { m.Status, m.NextAttemptAtUtc });
        builder.HasIndex(m => m.ParcelId);
        builder.HasIndex(m => m.CustomerId);
        builder.HasIndex(m => m.CreatedAtUtc);

        // The same parcel event is queued once per audience and channel, however many times it is attempted.
        builder.HasIndex(m => new { m.DedupeKey, m.Audience, m.Channel }).IsUnique().HasFilter("[DedupeKey] IS NOT NULL");

        // Delivery reports find their message by the provider's id.
        builder.HasIndex(m => new { m.Provider, m.ProviderMessageId }).IsUnique().HasFilter("[ProviderMessageId] IS NOT NULL");

        builder.HasOne<Parcel>().WithMany().HasForeignKey(m => m.ParcelId);
        builder.HasOne<Customer>().WithMany().HasForeignKey(m => m.CustomerId);
    }
}

internal sealed class NotificationLogConfiguration : IEntityTypeConfiguration<NotificationLog>
{
    public void Configure(EntityTypeBuilder<NotificationLog> builder)
    {
        builder.ToTable("NotificationLogs");
        builder.ConfigureEntity();

        builder.Property(l => l.RecipientMasked).HasMaxLength(100).IsRequired();
        builder.Property(l => l.Provider).HasMaxLength(50).IsRequired();
        builder.Property(l => l.ProviderMessageId).HasMaxLength(100);
        builder.Property(l => l.Error).HasMaxLength(1000);

        builder.HasIndex(l => l.OutboxMessageId);
        builder.HasIndex(l => l.CreatedAtUtc);

        builder.HasOne<NotificationOutboxMessage>().WithMany().HasForeignKey(l => l.OutboxMessageId);
    }
}

internal sealed class NotificationTemplateConfiguration : IEntityTypeConfiguration<NotificationTemplate>
{
    public void Configure(EntityTypeBuilder<NotificationTemplate> builder)
    {
        builder.ToTable("NotificationTemplates");
        builder.ConfigureEntity();

        builder.Property(t => t.Key).HasMaxLength(100).IsRequired();
        builder.Property(t => t.Language).HasMaxLength(10).IsRequired();
        builder.Property(t => t.Body).HasMaxLength(NotificationTemplate.MaxBodyLength).IsRequired();
        builder.Property(t => t.ProviderTemplateName).HasMaxLength(100);
        builder.Property(t => t.RowVersion).IsRowVersion();

        builder.HasIndex(t => new { t.Key, t.Channel, t.Language }).IsUnique();
    }
}

internal sealed class NotificationOptOutConfiguration : IEntityTypeConfiguration<NotificationOptOut>
{
    public void Configure(EntityTypeBuilder<NotificationOptOut> builder)
    {
        builder.ToTable("NotificationOptOuts");
        builder.ConfigureEntity();

        builder.Property(o => o.PhoneNumber).HasMaxLength(20).IsRequired();

        // One opt-out in force per number and channel; lifted ones stay as history.
        builder.HasIndex(o => new { o.PhoneNumber, o.Channel }).IsUnique().HasFilter("[RemovedAtUtc] IS NULL");
        builder.HasIndex(o => o.CreatedAtUtc);
    }
}

internal sealed class MockProviderMessageConfiguration : IEntityTypeConfiguration<MockProviderMessage>
{
    public void Configure(EntityTypeBuilder<MockProviderMessage> builder)
    {
        builder.ToTable("MockProviderMessages");
        builder.ConfigureEntity();

        builder.Property(m => m.ProviderMessageId).HasMaxLength(100).IsRequired();
        builder.Property(m => m.To).HasMaxLength(30).IsRequired();
        builder.Property(m => m.Body).HasMaxLength(2000).IsRequired();
        builder.Property(m => m.TemplateName).HasMaxLength(100);
        builder.Property(m => m.ParametersJson).HasColumnType("nvarchar(max)").IsRequired();

        builder.HasIndex(m => m.ProviderMessageId).IsUnique();
        builder.HasIndex(m => m.CreatedAtUtc);
    }
}

internal sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("WebhookEvents");
        builder.ConfigureEntity();

        builder.Property(e => e.Provider).HasMaxLength(50).IsRequired();
        builder.Property(e => e.EventId).HasMaxLength(100).IsRequired();
        builder.Property(e => e.EventType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.PayloadJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(e => e.Error).HasMaxLength(1000);

        // Idempotency: the same event from the same provider is stored once, so replays are detected.
        builder.HasIndex(e => new { e.Provider, e.EventId }).IsUnique();
        builder.HasIndex(e => new { e.Status, e.ReceivedAtUtc });
    }
}

internal sealed class ServiceAreaConfiguration : IEntityTypeConfiguration<ServiceArea>
{
    public void Configure(EntityTypeBuilder<ServiceArea> builder)
    {
        builder.ToTable("ServiceAreas", t => t.HasCheckConstraint("CK_ServiceAreas_Days", "[EstimatedDeliveryDays] >= 0 AND [EstimatedDeliveryDays] <= 30"));
        builder.ConfigureAuditableEntity();

        builder.Property(a => a.Code).HasMaxLength(ServiceArea.CodeMaxLength).IsRequired();
        builder.Property(a => a.Name).HasMaxLength(150).IsRequired();
        builder.Property(a => a.District).HasMaxLength(60).IsRequired();
        builder.Property(a => a.Municipality).HasMaxLength(100);

        builder.HasIndex(a => a.Code).IsUnique();
        builder.HasIndex(a => new { a.Province, a.District });
        builder.HasIndex(a => a.ServingBranchId);

        builder.HasOne<Branch>().WithMany().HasForeignKey(a => a.ServingBranchId);
    }
}

internal sealed class PricingRuleConfiguration : IEntityTypeConfiguration<PricingRule>
{
    public void Configure(EntityTypeBuilder<PricingRule> builder)
    {
        builder.ToTable("PricingRules", t =>
        {
            t.HasCheckConstraint("CK_PricingRules_Weight", "[MinWeightKg] >= 0 AND [MaxWeightKg] > [MinWeightKg]");
            t.HasCheckConstraint("CK_PricingRules_Prices", "[BasePrice] >= 0 AND [PricePerKg] >= 0");
            t.HasCheckConstraint("CK_PricingRules_CodFee", "[CodFeePercent] >= 0 AND [CodFeePercent] <= 100");
        });
        builder.ConfigureAuditableEntity();

        builder.Property(r => r.Name).HasMaxLength(150).IsRequired();
        builder.Property(r => r.MinWeightKg).HasPrecision(10, 3);
        builder.Property(r => r.MaxWeightKg).HasPrecision(10, 3);
        builder.Property(r => r.CodFeePercent).HasPrecision(5, 2);
        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.HasIndex(r => new { r.ServiceType, r.IsActive, r.EffectiveFromUtc });
        builder.HasIndex(r => new { r.FromServiceAreaId, r.ToServiceAreaId });

        builder.HasOne<ServiceArea>().WithMany().HasForeignKey(r => r.FromServiceAreaId);
        builder.HasOne<ServiceArea>().WithMany().HasForeignKey(r => r.ToServiceAreaId);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.ConfigureEntity();

        builder.Property(a => a.Action).HasMaxLength(50).IsRequired();
        builder.Property(a => a.EntityName).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(100);
        builder.Property(a => a.OldValues).HasColumnType("nvarchar(max)");
        builder.Property(a => a.NewValues).HasColumnType("nvarchar(max)");
        builder.Property(a => a.IpAddress).HasMaxLength(64);
        builder.Property(a => a.UserAgent).HasMaxLength(256);
        builder.Property(a => a.CorrelationId).HasMaxLength(64);

        // Deliberately no foreign keys: audit history must outlive the rows it refers to.
        builder.HasIndex(a => a.OccurredAtUtc);
        builder.HasIndex(a => new { a.EntityName, a.EntityId });
        builder.HasIndex(a => new { a.UserId, a.OccurredAtUtc });
        builder.HasIndex(a => a.Action);
    }
}
