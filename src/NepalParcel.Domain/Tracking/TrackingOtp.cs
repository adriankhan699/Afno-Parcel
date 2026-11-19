using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Tracking;

/// <summary>
/// A one-time code sent to a parcel's receiver so they can prove they are the receiver and see detailed tracking.
/// Only a keyed hash of the code is stored, never the code itself. A code works once, expires quickly and locks after a
/// few wrong guesses.
/// </summary>
public sealed class TrackingOtp : Entity
{
    private TrackingOtp()
    {
        CodeHash = string.Empty;
    }

    public Guid ParcelId { get; private init; }

    public string CodeHash { get; private init; }

    public DateTimeOffset CreatedAtUtc { get; private init; }

    public DateTimeOffset ExpiresAtUtc { get; private init; }

    public int FailedAttempts { get; private set; }

    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static TrackingOtp Create(Guid parcelId, string codeHash, DateTimeOffset nowUtc, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            throw new DomainException("A code must stay valid for a positive amount of time.");
        }

        return new TrackingOtp
        {
            ParcelId = Guard.NotEmpty(parcelId, "Parcel"),
            CodeHash = Guard.NotBlank(codeHash, "Code hash", 128),
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc.Add(lifetime),
        };
    }

    /// <summary>Not used yet, not expired, and not locked by too many wrong guesses.</summary>
    public bool IsUsable(DateTimeOffset nowUtc, int maxFailedAttempts) =>
        ConsumedAtUtc is null && nowUtc < ExpiresAtUtc && FailedAttempts < maxFailedAttempts;

    public void RegisterFailedAttempt() => FailedAttempts++;

    public void Consume(DateTimeOffset nowUtc)
    {
        if (ConsumedAtUtc is not null)
        {
            throw new DomainException("This code has already been used.");
        }

        ConsumedAtUtc = nowUtc;
    }
}
