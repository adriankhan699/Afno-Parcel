using Microsoft.AspNetCore.Identity;
using NepalParcel.Application.Auth;
using NepalParcel.Domain.Common;

namespace NepalParcel.Infrastructure.Identity;

internal sealed class IdentityService(UserManager<ApplicationUser> users, TimeProvider clock) : IIdentityService
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
