using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Branches;

/// <summary>
/// A physical location of the company (head office, branch counter or warehouse/hub).
/// Phase 1 foundation for the multi-branch / multi-warehouse network.
/// </summary>
public sealed class Branch : AuditableEntity
{
    public const int CodeMaxLength = 10;
    public const int NameMaxLength = 150;
    public const int EmailMaxLength = 256;

    // Required by EF Core.
    private Branch()
    {
        Code = string.Empty;
        Name = string.Empty;
        Address = null!;
        PhoneNumber = string.Empty;
    }

    private Branch(string code, string name, BranchType type, NepalAddress address, string phoneNumber, string? email)
    {
        Code = code;
        Name = name;
        Type = type;
        Address = address;
        PhoneNumber = phoneNumber;
        Email = email;
        IsActive = true;
    }

    /// <summary>Short unique code, upper-case (e.g. KTM01). Used in tracking numbers and labels later.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public BranchType Type { get; private set; }

    public NepalAddress Address { get; private set; }

    public string PhoneNumber { get; private set; }

    public string? Email { get; private set; }

    public bool IsActive { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static Branch Create(
        string code,
        string name,
        BranchType type,
        NepalAddress address,
        string phoneNumber,
        string? email = null)
    {
        ArgumentNullException.ThrowIfNull(address);

        var normalizedCode = Guard.NotBlank(code, "Branch code", CodeMaxLength).ToUpperInvariant();
        if (!normalizedCode.All(char.IsAsciiLetterOrDigit))
        {
            throw new DomainException("Branch code may contain only letters and digits.");
        }

        if (!Enum.IsDefined(type))
        {
            throw new DomainException($"'{type}' is not a valid branch type.");
        }

        if (!NepalPhoneNumber.TryNormalize(phoneNumber, out var phone))
        {
            throw new DomainException("Phone number is not a valid Nepali phone number.");
        }

        return new Branch(
            normalizedCode,
            Guard.NotBlank(name, "Branch name", NameMaxLength),
            type,
            address,
            phone,
            Guard.Optional(email, "Email", EmailMaxLength)?.ToLowerInvariant());
    }

    /// <summary>Changes the editable details. The code and the type are fixed once a branch exists.</summary>
    public void Update(string name, NepalAddress address, string phoneNumber, string? email)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!NepalPhoneNumber.TryNormalize(phoneNumber, out var phone))
        {
            throw new DomainException("Phone number is not a valid Nepali phone number.");
        }

        Name = Guard.NotBlank(name, "Branch name", NameMaxLength);
        Address = address;
        PhoneNumber = phone;
        Email = Guard.Optional(email, "Email", EmailMaxLength)?.ToLowerInvariant();
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
