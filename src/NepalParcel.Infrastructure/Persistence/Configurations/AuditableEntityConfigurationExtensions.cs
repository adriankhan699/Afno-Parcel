using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NepalParcel.Domain.Common;

namespace NepalParcel.Infrastructure.Persistence.Configurations;

internal static class AuditableEntityConfigurationExtensions
{
    public const int AuditUserMaxLength = 100;

    /// <summary>Maps the primary key (generated in the domain, never by the database).</summary>
    public static void ConfigureEntity<T>(this EntityTypeBuilder<T> builder)
        where T : Entity
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
    }

    /// <summary>Maps the common key and audit columns shared by all auditable entities.</summary>
    public static void ConfigureAuditableEntity<T>(this EntityTypeBuilder<T> builder)
        where T : AuditableEntity
    {
        builder.ConfigureEntity();
        builder.Property(e => e.CreatedAtUtc).IsRequired();
        builder.Property(e => e.CreatedBy).HasMaxLength(AuditUserMaxLength);
        builder.Property(e => e.UpdatedBy).HasMaxLength(AuditUserMaxLength);
    }

    /// <summary>Maps a <see cref="NepalAddress"/> owned value object with a column prefix.</summary>
    public static void OwnsNepalAddress<T>(
        this EntityTypeBuilder<T> builder,
        Expression<Func<T, NepalAddress?>> navigation,
        string columnPrefix,
        bool required = true)
        where T : class
    {
        builder.OwnsOne<NepalAddress>(navigation, address =>
        {
            address.Property(a => a.Province)
                .HasColumnName($"{columnPrefix}Province")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
            address.Property(a => a.District)
                .HasColumnName($"{columnPrefix}District")
                .HasMaxLength(NepalAddress.DistrictMaxLength)
                .IsRequired();
            address.Property(a => a.Municipality)
                .HasColumnName($"{columnPrefix}Municipality")
                .HasMaxLength(NepalAddress.MunicipalityMaxLength)
                .IsRequired();
            address.Property(a => a.WardNumber)
                .HasColumnName($"{columnPrefix}WardNumber")
                .IsRequired();
            address.Property(a => a.StreetOrTole)
                .HasColumnName($"{columnPrefix}StreetOrTole")
                .HasMaxLength(NepalAddress.StreetMaxLength);
            address.Property(a => a.Landmark)
                .HasColumnName($"{columnPrefix}Landmark")
                .HasMaxLength(NepalAddress.LandmarkMaxLength);
            address.Property(a => a.PostalCode)
                .HasColumnName($"{columnPrefix}PostalCode")
                .HasMaxLength(NepalAddress.PostalCodeLength)
                .IsFixedLength();
        });

        if (required)
        {
            builder.Navigation(navigation!).IsRequired();
        }
    }
}
