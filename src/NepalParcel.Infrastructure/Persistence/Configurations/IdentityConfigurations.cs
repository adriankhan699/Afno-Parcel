using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NepalParcel.Domain.Identity;
using NepalParcel.Infrastructure.Identity;

namespace NepalParcel.Infrastructure.Persistence.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.FullName).HasMaxLength(ApplicationUser.FullNameMaxLength).IsRequired();
        builder.Property(u => u.CreatedAtUtc).IsRequired();
        builder.HasIndex(u => u.PhoneNumber);
    }
}

/// <summary>Seeds the application roles. Ids and concurrency stamps are fixed so the migration is deterministic.</summary>
internal sealed class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public static readonly IReadOnlyDictionary<string, Guid> RoleIds = new Dictionary<string, Guid>(StringComparer.Ordinal)
    {
        [Roles.SuperAdmin] = Guid.Parse("a1000000-0000-4000-8000-000000000001"),
        [Roles.BranchManager] = Guid.Parse("a1000000-0000-4000-8000-000000000002"),
        [Roles.BookingStaff] = Guid.Parse("a1000000-0000-4000-8000-000000000003"),
        [Roles.WarehouseStaff] = Guid.Parse("a1000000-0000-4000-8000-000000000004"),
        [Roles.DeliveryAgent] = Guid.Parse("a1000000-0000-4000-8000-000000000005"),
        [Roles.Accountant] = Guid.Parse("a1000000-0000-4000-8000-000000000006"),
        [Roles.Customer] = Guid.Parse("a1000000-0000-4000-8000-000000000007"),
    };

    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        builder.HasData(Roles.All.Select(name => new ApplicationRole
        {
            Id = RoleIds[name],
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            ConcurrencyStamp = RoleIds[name].ToString(),
        }));
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens", NepalParcelDbContext.IdentitySchema);
        builder.ConfigureEntity();

        builder.Property(t => t.TokenHash).HasMaxLength(RefreshToken.HashLength).IsFixedLength().IsRequired();
        builder.Property(t => t.CreatedByIp).HasMaxLength(64);
        builder.Property(t => t.RevokedReason).HasMaxLength(100);
        // Detects two requests rotating the same token at the same time.
        builder.Property(t => t.RowVersion).IsRowVersion();
        builder.Ignore(t => t.IsRevoked);

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.FamilyId);
        builder.HasIndex(t => new { t.UserId, t.ExpiresAtUtc });

        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.UserId).IsRequired();
    }
}
