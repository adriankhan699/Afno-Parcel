using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Customers;

public enum ConsentType
{
    TermsOfService = 1,
    PrivacyPolicy = 2,
    SmsNotifications = 3,
    EmailNotifications = 4,
    Marketing = 5,
    WhatsAppNotifications = 6,
}

public enum ConsentSource
{
    Web = 1,
    Counter = 2,
    Mobile = 3,
}

/// <summary>
/// An immutable consent decision. Withdrawing consent adds a new row with <see cref="IsGranted"/> = false, so the
/// full history is preserved; the latest row per (customer, type) is the current state.
/// </summary>
public sealed class CustomerConsent : Entity
{
    private CustomerConsent()
    {
        PolicyVersion = string.Empty;
    }

    public Guid CustomerId { get; private init; }

    public ConsentType Type { get; private init; }

    public bool IsGranted { get; private init; }

    public string PolicyVersion { get; private init; }

    public ConsentSource Source { get; private init; }

    public DateTimeOffset RecordedAtUtc { get; private init; }

    public string? IpAddress { get; private init; }

    public static CustomerConsent Record(
        Guid customerId,
        ConsentType type,
        bool isGranted,
        string policyVersion,
        ConsentSource source,
        DateTimeOffset recordedAtUtc,
        string? ipAddress = null)
    {
        if (!Enum.IsDefined(type))
        {
            throw new DomainException($"'{type}' is not a valid consent type.");
        }

        if (!Enum.IsDefined(source))
        {
            throw new DomainException($"'{source}' is not a valid consent source.");
        }

        return new CustomerConsent
        {
            CustomerId = Guard.NotEmpty(customerId, "Customer"),
            Type = type,
            IsGranted = isGranted,
            PolicyVersion = Guard.NotBlank(policyVersion, "Policy version", 20),
            Source = source,
            RecordedAtUtc = recordedAtUtc,
            IpAddress = Guard.Optional(ipAddress, "IP address", 64),
        };
    }
}
