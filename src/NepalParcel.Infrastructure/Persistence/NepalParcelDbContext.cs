using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Integrations;
using NepalParcel.Domain.Notifications;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;
using NepalParcel.Domain.Pricing;
using NepalParcel.Domain.Tracking;
using NepalParcel.Domain.Warehouses;
using NepalParcel.Infrastructure.Identity;

namespace NepalParcel.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for the modular monolith (ASP.NET Core Identity + business tables).
/// Business tables live in the <c>parcel</c> schema and identity tables in the <c>identity</c> schema.
/// Each module keeps its own <c>IEntityTypeConfiguration</c> classes; they are discovered automatically.
/// </summary>
public sealed class NepalParcelDbContext(DbContextOptions<NepalParcelDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public const string DefaultSchema = "parcel";
    public const string IdentitySchema = "identity";

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<CustomerConsent> CustomerConsents => Set<CustomerConsent>();

    public DbSet<ParcelStatus> ParcelStatuses => Set<ParcelStatus>();

    public DbSet<Parcel> Parcels => Set<Parcel>();

    public DbSet<ParcelItem> ParcelItems => Set<ParcelItem>();

    public DbSet<ParcelStatusHistory> ParcelStatusHistory => Set<ParcelStatusHistory>();

    public DbSet<DeliveryAgent> DeliveryAgents => Set<DeliveryAgent>();

    public DbSet<DeliveryAssignment> DeliveryAssignments => Set<DeliveryAssignment>();

    public DbSet<DeliveryProof> DeliveryProofs => Set<DeliveryProof>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();

    public DbSet<CodCollection> CodCollections => Set<CodCollection>();

    public DbSet<CodSettlement> CodSettlements => Set<CodSettlement>();

    public DbSet<NotificationOutboxMessage> NotificationOutbox => Set<NotificationOutboxMessage>();

    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();

    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    public DbSet<PricingRule> PricingRules => Set<PricingRule>();

    public DbSet<ServiceArea> ServiceAreas => Set<ServiceArea>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<TrackingOtp> TrackingOtps => Set<TrackingOtp>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(DefaultSchema);
        MoveIdentityTablesToIdentitySchema(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NepalParcelDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            // History and financial records must never disappear because a parent row was deleted.
            foreach (var foreignKey in entityType.GetForeignKeys().Where(fk => !fk.IsOwnership))
            {
                foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
            }

            foreach (var property in entityType.GetProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clrType.IsEnum && property.GetValueConverter() is null && property.GetProviderClrType() is null)
                {
                    // Enums are stored as readable text (e.g. 'InTransit') rather than numbers.
                    property.SetProviderClrType(typeof(string));
                    if (property.GetMaxLength() is null)
                    {
                        property.SetMaxLength(40);
                    }
                }
            }
        }

        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            // SQLite is used only by the integration tests. It has no rowversion type, does not understand
            // 'nvarchar(max)' and cannot sort or compare DateTimeOffset values, and stores decimals as text, so adapt those four things;
            // everything else in the model is identical to production.
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clrType == typeof(DateTimeOffset))
                {
                    // UTC ticks: exact to 100 ns, so values round-trip unchanged, and they sort and compare numerically.
                    property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(
                        v => v.UtcTicks,
                        v => new DateTimeOffset(v, TimeSpan.Zero)));
                }
                else if (clrType == typeof(decimal))
                {
                    // SQLite stores decimals as text, which compares '100.000' < '20.000'. Numbers keep check constraints,
                    // comparisons and sorting correct, as they are on SQL Server.
                    property.SetValueConverter(new ValueConverter<decimal, double>(v => (double)v, v => (decimal)v));
                }

                if (property.IsConcurrencyToken && property.ClrType == typeof(byte[]))
                {
                    property.ValueGenerated = ValueGenerated.Never;
                }

                if (string.Equals(property.GetColumnType(), "nvarchar(max)", StringComparison.OrdinalIgnoreCase))
                {
                    property.SetColumnType(null);
                }
            }
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Money-safe decimals (NPR, 2 places) and bounded strings unless a configuration says otherwise.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        configurationBuilder.Properties<string>().HaveMaxLength(256);
    }

    private static void MoveIdentityTablesToIdentitySchema(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationUser>().ToTable("Users", IdentitySchema);
        modelBuilder.Entity<ApplicationRole>().ToTable("Roles", IdentitySchema);
        modelBuilder.Entity<IdentityUserRole<Guid>>().ToTable("UserRoles", IdentitySchema);
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("UserClaims", IdentitySchema);
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("UserLogins", IdentitySchema);
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("UserTokens", IdentitySchema);
        modelBuilder.Entity<IdentityRoleClaim<Guid>>().ToTable("RoleClaims", IdentitySchema);
    }
}
