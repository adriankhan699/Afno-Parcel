using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Identity;

/// <summary>
/// A single-use refresh token. Only a SHA-256 hash of the token is stored. Tokens issued from the same login
/// share a <see cref="FamilyId"/>; presenting an already-used token revokes the whole family (theft detection).
/// </summary>
public sealed class RefreshToken : Entity
{
    public const int HashLength = 64;

    private RefreshToken()
    {
        TokenHash = string.Empty;
    }

    private RefreshToken(Guid userId, Guid familyId, string tokenHash, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc, string? createdByIp)
    {
        UserId = Guard.NotEmpty(userId, "User id");
        FamilyId = familyId;
        TokenHash = tokenHash;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        CreatedByIp = Guard.Optional(createdByIp, "IP address", 64);
    }

    public Guid UserId { get; private set; }

    public Guid FamilyId { get; private set; }

    public string TokenHash { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public string? CreatedByIp { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public string? RevokedReason { get; private set; }

    public Guid? ReplacedByTokenId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public bool IsRevoked => RevokedAtUtc is not null;

    public bool IsExpired(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc;

    public bool IsActive(DateTimeOffset nowUtc) => !IsRevoked && !IsExpired(nowUtc);

    /// <summary>Starts a new family (a fresh login).</summary>
    public static RefreshToken Issue(Guid userId, string tokenHash, DateTimeOffset nowUtc, TimeSpan lifetime, string? ip) =>
        new(userId, Guid.CreateVersion7(), ValidateHash(tokenHash), nowUtc, nowUtc.Add(lifetime), ip);

    /// <summary>Issues the next token in the same family after rotation.</summary>
    public RefreshToken Rotate(string newTokenHash, DateTimeOffset nowUtc, TimeSpan lifetime, string? ip)
    {
        var next = new RefreshToken(UserId, FamilyId, ValidateHash(newTokenHash), nowUtc, nowUtc.Add(lifetime), ip);
        Revoke(nowUtc, "Rotated", next.Id);
        return next;
    }

    public void Revoke(DateTimeOffset nowUtc, string reason, Guid? replacedByTokenId = null)
    {
        if (IsRevoked)
        {
            return;
        }

        RevokedAtUtc = nowUtc;
        RevokedReason = Guard.NotBlank(reason, "Reason", 100);
        ReplacedByTokenId = replacedByTokenId;
    }

    private static string ValidateHash(string hash) =>
        hash is { Length: HashLength } ? hash : throw new DomainException("Token hash must be a 64 character SHA-256 hex string.");
}
