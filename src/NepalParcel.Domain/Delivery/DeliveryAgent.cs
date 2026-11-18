using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Delivery;

/// <summary>A rider/driver profile linked to an Identity user with the DeliveryAgent role.</summary>
public sealed class DeliveryAgent : AuditableEntity
{
    public const int NameMaxLength = 150;

    private DeliveryAgent()
    {
        FullName = string.Empty;
        PhoneNumber = string.Empty;
    }

    public Guid UserId { get; private set; }

    public Guid BranchId { get; private set; }

    public string FullName { get; private set; }

    public string PhoneNumber { get; private set; }

    public string? VehicleType { get; private set; }

    public string? VehicleNumber { get; private set; }

    public string? LicenseNumber { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Whether the agent can currently take new assignments.</summary>
    public bool IsAvailable { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static DeliveryAgent Create(
        Guid userId,
        Guid branchId,
        string fullName,
        string phoneNumber,
        string? vehicleType = null,
        string? vehicleNumber = null,
        string? licenseNumber = null) =>
        new()
        {
            UserId = Guard.NotEmpty(userId, "User"),
            BranchId = Guard.NotEmpty(branchId, "Branch"),
            FullName = Guard.NotBlank(fullName, "Full name", NameMaxLength),
            PhoneNumber = Guard.NormalizePhone(phoneNumber),
            VehicleType = Guard.Optional(vehicleType, "Vehicle type", 40),
            VehicleNumber = Guard.Optional(vehicleNumber, "Vehicle number", 30)?.ToUpperInvariant(),
            LicenseNumber = Guard.Optional(licenseNumber, "License number", 40),
            IsActive = true,
            IsAvailable = true,
        };

    public void SetAvailability(bool isAvailable)
    {
        if (!IsActive && isAvailable)
        {
            throw new DomainException("An inactive agent cannot be made available.");
        }

        IsAvailable = isAvailable;
    }

    public void Deactivate()
    {
        IsActive = false;
        IsAvailable = false;
    }
}
