using Microsoft.AspNetCore.Identity;
using NepalParcel.Application.Auth;
using Microsoft.EntityFrameworkCore;
using NepalParcel.Application.Network;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;
using NepalParcel.Infrastructure.Persistence;

namespace NepalParcel.Infrastructure.Identity;

internal sealed class IdentityService(UserManager<ApplicationUser> users, NepalParcelDbContext db, TimeProvider clock) : IIdentityService
{
    public async Task<CreateUserResult> CreateUserAsync(
        string fullName,
        string email,
        string? phoneNumber,
        string password,
        string role,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        string? phone = null;
        if (!string.IsNullOrWhiteSpace(phoneNumber) && NepalPhoneNumber.TryNormalize(phoneNumber, out var normalizedPhone))
        {
            phone = normalizedPhone;
        }

        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = normalizedEmail,
            Email = normalizedEmail,
            FullName = fullName,
            PhoneNumber = phone,
            IsActive = true,
            CreatedAtUtc = clock.GetUtcNow(),
        };

        var created = await users.CreateAsync(user, password).ConfigureAwait(false);
        if (!created.Succeeded)
        {
            return new CreateUserResult(null, ToFailures(created));
        }

        var roleAdded = await users.AddToRoleAsync(user, role).ConfigureAwait(false);
        if (!roleAdded.Succeeded)
        {
            await users.DeleteAsync(user).ConfigureAwait(false);
            return new CreateUserResult(null, ToFailures(roleAdded));
        }

        return new CreateUserResult(ToInfo(user, [role]), []);
    }

    public async Task<PasswordCheckResult> CheckPasswordAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByEmailAsync(email).ConfigureAwait(false);
        if (user is null)
        {
            // Spend the same effort as a real check so response time does not reveal whether the account exists.
            _ = users.PasswordHasher.HashPassword(new ApplicationUser(), password);
            return new PasswordCheckResult(PasswordCheckStatus.InvalidCredentials, null);
        }

        if (!user.IsActive)
        {
            return new PasswordCheckResult(PasswordCheckStatus.Inactive, null);
        }

        if (await users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            return new PasswordCheckResult(PasswordCheckStatus.LockedOut, null);
        }

        if (!await users.CheckPasswordAsync(user, password).ConfigureAwait(false))
        {
            await users.AccessFailedAsync(user).ConfigureAwait(false);
            var nowLocked = await users.IsLockedOutAsync(user).ConfigureAwait(false);
            return new PasswordCheckResult(nowLocked ? PasswordCheckStatus.LockedOut : PasswordCheckStatus.InvalidCredentials, null);
        }

        await users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
        var roles = await users.GetRolesAsync(user).ConfigureAwait(false);
        return new PasswordCheckResult(PasswordCheckStatus.Success, ToInfo(user, roles.ToList()));
    }

    public async Task<UserInfo?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(id.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        var roles = await users.GetRolesAsync(user).ConfigureAwait(false);
        return ToInfo(user, roles.ToList());
    }

    public async Task SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(id.ToString()).ConfigureAwait(false);
        if (user is null || user.IsActive == isActive)
        {
            return;
        }

        user.IsActive = isActive;
        var updated = await users.UpdateAsync(user).ConfigureAwait(false);
        if (!updated.Succeeded)
        {
            throw new InvalidOperationException("The account could not be updated: " + string.Join("; ", updated.Errors.Select(e => e.Description)));
        }
    }

    public async Task<(IReadOnlyList<UserInfo> Items, int TotalCount)> ListStaffAsync(
        StaffUserFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Staff are everyone with a role other than Customer. A role filter narrows that to one role.
        var staffRoles = Roles.Staff.ToArray();
        var roleNames = string.IsNullOrWhiteSpace(filter.Role)
            ? staffRoles
            : staffRoles.Where(r => string.Equals(r, filter.Role.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

        var staffUserIds = db.UserRoles
            .Join(db.Roles.Where(r => roleNames.Contains(r.Name!)), ur => ur.RoleId, r => r.Id, (ur, _) => ur.UserId)
            .Distinct();
        var query = db.Users.AsNoTracking().Where(u => staffUserIds.Contains(u.Id));

        if (filter.OnlyUserIds is { } only)
        {
            query = query.Where(u => only.Contains(u.Id));
        }

        if (filter.ExcludeUserIds is { Count: > 0 } exclude)
        {
            query = query.Where(u => !exclude.Contains(u.Id));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var like = "%" + filter.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(u => EF.Functions.Like(u.FullName, like, "\\") || EF.Functions.Like(u.Email!, like, "\\"));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var found = await query
            .OrderBy(u => u.FullName)
            .ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ids = found.Select(u => u.Id).ToList();
        var roleRows = await db.UserRoles
            .Where(ur => ids.Contains(ur.UserId))
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var rolesByUser = roleRows
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name!).Order(StringComparer.Ordinal).ToList());

        return (found.Select(u => ToInfo(u, rolesByUser.GetValueOrDefault(u.Id, []))).ToList(), total);
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(id.ToString()).ConfigureAwait(false);
        if (user is not null)
        {
            await users.DeleteAsync(user).ConfigureAwait(false);
        }
    }

    private static UserInfo ToInfo(ApplicationUser user, IReadOnlyList<string> roles) =>
        new(user.Id, user.Email ?? string.Empty, user.FullName, user.PhoneNumber, roles, user.IsActive, user.CreatedAtUtc);

    private static List<IdentityFailure> ToFailures(IdentityResult result) =>
        result.Errors.Select(e => new IdentityFailure(e.Code, e.Description)).ToList();
}
