using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Warehouses;

/// <summary>A storage / sorting facility operated by a branch (hub, godown).</summary>
public sealed class Warehouse : AuditableEntity
{
    public const int CodeMaxLength = 10;
    public const int NameMaxLength = 150;

    private Warehouse()
    {
        Code = string.Empty;
        Name = string.Empty;
        Address = null!;
        PhoneNumber = string.Empty;
    }

    private Warehouse(Guid branchId, string code, string name, NepalAddress address, string phoneNumber, int? capacityParcels)
    {
        BranchId = branchId;
        Code = code;
        Name = name;
        Address = address;
        PhoneNumber = phoneNumber;
        CapacityParcels = capacityParcels;
        IsActive = true;
    }

    public Guid BranchId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public NepalAddress Address { get; private set; }

    public string PhoneNumber { get; private set; }

    public int? CapacityParcels { get; private set; }

    public bool IsActive { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static Warehouse Create(Guid branchId, string code, string name, NepalAddress address, string phoneNumber, int? capacityParcels = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (capacityParcels is <= 0)
        {
            throw new DomainException("Warehouse capacity must be greater than zero.");
        }

        var normalizedCode = Guard.NotBlank(code, "Warehouse code", CodeMaxLength).ToUpperInvariant();
        if (!normalizedCode.All(char.IsAsciiLetterOrDigit))
        {
            throw new DomainException("Warehouse code may contain only letters and digits.");
        }

        return new Warehouse(
            Guard.NotEmpty(branchId, "Branch"),
            normalizedCode,
            Guard.NotBlank(name, "Warehouse name", NameMaxLength),
            address,
            Guard.NormalizePhone(phoneNumber),
            capacityParcels);
    }

    /// <summary>Changes the editable details. The code and the operating branch are fixed once a warehouse exists.</summary>
    public void Update(string name, NepalAddress address, string phoneNumber, int? capacityParcels)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (capacityParcels is <= 0)
        {
            throw new DomainException("Warehouse capacity must be greater than zero.");
        }

        Name = Guard.NotBlank(name, "Warehouse name", NameMaxLength);
        Address = address;
        PhoneNumber = Guard.NormalizePhone(phoneNumber);
        CapacityParcels = capacityParcels;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
