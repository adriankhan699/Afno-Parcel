using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Delivery;

public enum ProofType
{
    Signature = 1,
    Photo = 2,
    Otp = 3,
}

/// <summary>
/// Evidence that a parcel was handed over. Files are kept in private storage and referenced by
/// <see cref="StorageKey"/>; they are never served from a public URL.
/// </summary>
public sealed class DeliveryProof : Entity
{
    private DeliveryProof()
    {
    }

    public Guid ParcelId { get; private init; }

    public Guid? AssignmentId { get; private init; }

    public Guid? CapturedByAgentId { get; private init; }

    public ProofType Type { get; private init; }

    /// <summary>Key/path of the private file (signature image or photo). Null for OTP proofs.</summary>
    public string? StorageKey { get; private init; }

    public string? RecipientName { get; private init; }

    public bool OtpVerified { get; private init; }

    public DateTimeOffset CapturedAtUtc { get; private init; }

    public static DeliveryProof Create(
        Guid parcelId,
        ProofType type,
        DateTimeOffset capturedAtUtc,
        Guid? assignmentId = null,
        Guid? capturedByAgentId = null,
        string? storageKey = null,
        string? recipientName = null,
        bool otpVerified = false)
    {
        if (!Enum.IsDefined(type))
        {
            throw new DomainException($"'{type}' is not a valid proof type.");
        }

        if (type is ProofType.Signature or ProofType.Photo && string.IsNullOrWhiteSpace(storageKey))
        {
            throw new DomainException($"A {type} proof requires a stored file.");
        }

        if (type == ProofType.Otp && !otpVerified)
        {
            throw new DomainException("An OTP proof must be verified.");
        }

        return new DeliveryProof
        {
            ParcelId = Guard.NotEmpty(parcelId, "Parcel"),
            AssignmentId = assignmentId,
            CapturedByAgentId = capturedByAgentId,
            Type = type,
            StorageKey = Guard.Optional(storageKey, "Storage key", 300),
            RecipientName = Guard.Optional(recipientName, "Recipient name", 150),
            OtpVerified = otpVerified,
            CapturedAtUtc = capturedAtUtc,
        };
    }
}
