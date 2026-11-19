namespace NepalParcel.Domain.Parcels;

/// <summary>Turns a service type and the destination's typical delivery time into an estimate shown to the customer.</summary>
public static class DeliveryEstimator
{
    /// <summary>Same-day parcels are promised within this many hours of booking.</summary>
    public const int SameDayHours = 10;

    /// <summary>Whole days promised for the service type (0 for same-day).</summary>
    public static int EstimatedDays(ServiceType serviceType, int areaDeliveryDays) => serviceType switch
    {
        ServiceType.SameDay => 0,
        ServiceType.Express => Math.Max(1, (int)Math.Ceiling(areaDeliveryDays / 2.0)),
        _ => Math.Max(1, areaDeliveryDays),
    };

    public static DateTimeOffset Estimate(DateTimeOffset bookedAtUtc, ServiceType serviceType, int areaDeliveryDays) =>
        serviceType == ServiceType.SameDay
            ? bookedAtUtc.AddHours(SameDayHours)
            : bookedAtUtc.AddDays(EstimatedDays(serviceType, areaDeliveryDays));
}

/// <summary>Business rules for cancelling a booking.</summary>
public static class CancellationPolicy
{
    public const int ReasonMinLength = 5;
    public const int ReasonMaxLength = 300;

    /// <summary>
    /// Customers may cancel only while the booking has just been created (not yet picked up or handed to us). Branch
    /// staff may also cancel after pickup, but never once the parcel has been shipped. Terminal statuses can never be
    /// cancelled. This is a subset of what <see cref="ParcelStatusWorkflow"/> permits.
    /// </summary>
    public static bool CanCancel(ParcelStatusCode status, bool byStaff) =>
        status == ParcelStatusCode.BookingCreated ||
        (byStaff && status == ParcelStatusCode.PickedUp);

    public static string Explain(ParcelStatusCode status, bool byStaff) =>
        status == ParcelStatusCode.Cancelled
            ? "This booking is already cancelled."
            : byStaff
                ? $"A parcel that is '{ParcelStatusCatalog.Get(status).Name}' can no longer be cancelled."
                : "A booking can be cancelled only before it is picked up. Please contact a branch for help.";
}
