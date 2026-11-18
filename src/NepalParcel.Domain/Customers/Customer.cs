using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Customers;

public enum CustomerType
{
    Individual = 1,
    Business = 2,
}

/// <summary>
/// A sender/account holder. Online customers are linked to an Identity user (<see cref="UserId"/>);
/// walk-in customers booked at a counter have no login. Contact details are personal data and must never be
/// returned by public (unauthenticated) endpoints.
/// </summary>
public sealed class Customer : AuditableEntity
{
    public const int NameMaxLength = 150;
    public const int EmailMaxLength = 256;

    private Customer()
    {
        FullName = string.Empty;
        PhoneNumber = string.Empty;
    }

    private Customer(Guid? userId, CustomerType type, string fullName, string? companyName, string phoneNumber, string? email, NepalAddress? address)
    {
        UserId = userId;
        Type = type;
        FullName = fullName;
        CompanyName = companyName;
        PhoneNumber = phoneNumber;
        Email = email;
        Address = address;
        IsActive = true;
    }

    /// <summary>Identity user id for online customers; null for walk-in customers.</summary>
    public Guid? UserId { get; private set; }

    public CustomerType Type { get; private set; }

    public string FullName { get; private set; }

    public string? CompanyName { get; private set; }

    public string PhoneNumber { get; private set; }

    public string? Email { get; private set; }

    public NepalAddress? Address { get; private set; }

    public bool IsActive { get; private set; }

    public bool IsWalkIn => UserId is null;

    public byte[] RowVersion { get; private set; } = [];

    public static Customer Create(
        string fullName,
        string phoneNumber,
        string? email = null,
        Guid? userId = null,
        NepalAddress? address = null,
        CustomerType type = CustomerType.Individual,
        string? companyName = null)
    {
        if (!Enum.IsDefined(type))
        {
            throw new DomainException($"'{type}' is not a valid customer type.");
        }

        if (type == CustomerType.Business && string.IsNullOrWhiteSpace(companyName))
        {
            throw new DomainException("Company name is required for business customers.");
        }

        return new Customer(
            userId,
            type,
            Guard.NotBlank(fullName, "Full name", NameMaxLength),
            Guard.Optional(companyName, "Company name", NameMaxLength),
            Guard.NormalizePhone(phoneNumber),
            Guard.Optional(email, "Email", EmailMaxLength)?.ToLowerInvariant(),
            address);
    }

    public void UpdateContact(string fullName, string phoneNumber, NepalAddress? address)
    {
        FullName = Guard.NotBlank(fullName, "Full name", NameMaxLength);
        PhoneNumber = Guard.NormalizePhone(phoneNumber);
        Address = address;
    }

    public void Deactivate() => IsActive = false;
}
