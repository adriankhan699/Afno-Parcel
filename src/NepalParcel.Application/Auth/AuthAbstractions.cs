using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Identity;

namespace NepalParcel.Application.Auth;

/// <summary>A user as seen by the application layer (independent of ASP.NET Identity types).</summary>
public sealed record UserInfo(
    Guid Id,
    string Email,
    string FullName,
    string? PhoneNumber,
    IReadOnlyList<string> Roles,
    bool IsActive,
    DateTimeOffset CreatedAtUtc);

public sealed record IdentityFailure(string Code, string Description);

public sealed record CreateUserResult(UserInfo? User, IReadOnlyList<IdentityFailure> Failures)
{
    public bool Succeeded => User is not null;
}

public enum PasswordCheckStatus
{
    Success,
    InvalidCredentials,
    LockedOut,
    Inactive,
}

public sealed record PasswordCheckResult(PasswordCheckStatus Status, UserInfo? User);

/// <summary>Account management backed by ASP.NET Core Identity (implemented in Infrastructure).</summary>
public interface IIdentityService
{
    Task<CreateUserResult> CreateUserAsync(
        string fullName,
        string email,
        string? phoneNumber,
        string password,
        string role,
        CancellationToken cancellationToken = default);

    /// <summary>Verifies the password, applying account lockout after repeated failures.</summary>
    Task<PasswordCheckResult> CheckPasswordAsync(string email, string password, CancellationToken cancellationToken = default);

    Task<UserInfo?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task DeleteUserAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record AccessTokenResult(string Token, DateTimeOffset ExpiresAtUtc);

/// <summary>Creates access tokens (JWT) and refresh tokens.</summary>
public interface ITokenService
{
    TimeSpan RefreshTokenLifetime { get; }

    AccessTokenResult CreateAccessToken(UserInfo user);

    /// <summary>Returns a new cryptographically random opaque refresh token.</summary>
    string GenerateRefreshToken();

    /// <summary>One-way hash (hex) used to store and look up refresh tokens.</summary>
    string HashRefreshToken(string refreshToken);
}

public interface IRefreshTokenRepository
{
    void Add(RefreshToken token);

    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RefreshToken>> GetActiveByFamilyAsync(Guid familyId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

public interface ICustomerRepository
{
    void Add(Customer customer);

    Task<Customer?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);
}

public interface IAuditLogRepository
{
    void Add(AuditLog entry);
}
