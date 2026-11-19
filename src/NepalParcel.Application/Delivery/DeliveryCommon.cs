using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Contracts.Delivery;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Application.Delivery;

/// <summary>
/// Works out which delivery agent is calling. Everything an agent does goes through here, so a login without an active agent
/// profile (a customer, another kind of staff, or an agent who has been switched off) never reaches a parcel.
/// </summary>
internal sealed class AgentContext(IDeliveryAgentRepository agents, ICurrentUser currentUser)
{
    public async Task<DeliveryAgent> RequireAgentAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException("Authentication is required.");
        if (!currentUser.IsInRole(Roles.DeliveryAgent))
        {
            throw new ForbiddenException("Only delivery agents can do this.");
        }

        var agent = await agents.GetByUserIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            throw new ForbiddenException("You do not have a delivery agent profile. Ask a manager to set one up.");
        }

        if (!agent.IsActive)
        {
            throw new ForbiddenException("Your delivery agent account is switched off. Ask a manager.");
        }

        return agent;
    }
}

internal static class NepalDay
{
    /// <summary>The instant the Nepal calendar day containing <paramref name="instant"/> began.</summary>
    public static DateTimeOffset StartUtc(DateTimeOffset instant) => StartUtc(DateOnly.FromDateTime(instant.ToOffset(NepalTime.Offset).DateTime));

    public static DateTimeOffset StartUtc(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), NepalTime.Offset).ToUniversalTime();

    public static DateOnly Today(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(NepalTime.Offset).DateTime);
}

internal static class DeliveryMappings
{
    public static DeliveryProofResponse ToResponse(this DeliveryProof proof) =>
        new(proof.Id, proof.Type.ToString(), proof.RecipientName, proof.OtpVerified, !string.IsNullOrEmpty(proof.StorageKey), proof.CapturedAtUtc);

    /// <summary>The receiver's full address as one line, for the rider's eyes only.</summary>
    public static string FullAddress(this Parcel parcel)
    {
        var address = parcel.ReceiverAddress;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(address.StreetOrTole))
        {
            parts.Add(address.StreetOrTole);
        }

        if (!string.IsNullOrWhiteSpace(address.Landmark))
        {
            parts.Add($"near {address.Landmark}");
        }

        parts.Add($"{address.Municipality}-{address.WardNumber}");
        parts.Add(address.District);
        return string.Join(", ", parts);
    }

    public static string Area(this Parcel parcel) =>
        $"{parcel.ReceiverAddress.Municipality}-{parcel.ReceiverAddress.WardNumber}, {parcel.ReceiverAddress.District}";

    public static AgentAssignmentResponse ToAgentResponse(
        this DeliveryAssignment assignment,
        Parcel parcel,
        CodCollection? cod,
        IReadOnlyList<DeliveryProof> proofs,
        bool returnPending)
    {
        var inProgress = assignment.Status == AssignmentStatus.InProgress && parcel.Status == ParcelStatusCode.OutForDelivery;
        var codCollected = cod is not null;
        var hasProof = proofs.Count > 0;

        // The phone number and street address are needed only while the job is open. Afterwards they are not sent at all.
        var open = assignment.IsOpen;
        return new AgentAssignmentResponse(
            assignment.Id,
            parcel.Id,
            parcel.TrackingNumber,
            assignment.Status.ToString(),
            assignment.AttemptNumber,
            assignment.AssignedAtUtc,
            assignment.StartedAtUtc,
            assignment.CompletedAtUtc,
            assignment.FailureReason,
            parcel.Status.ToString(),
            parcel.ReceiverName,
            open ? parcel.ReceiverPhone : null,
            open ? parcel.FullAddress() : null,
            open ? parcel.ReceiverAddressInstructions : null,
            parcel.Area(),
            parcel.ServiceType.ToString(),
            parcel.Description,
            parcel.Quantity,
            parcel.WeightKg,
            parcel.CodAmount,
            codCollected,
            cod?.CollectedAmount,
            hasProof,
            proofs.Select(p => p.ToResponse()).ToList(),
            CanStart: assignment.Status is AssignmentStatus.Assigned or AssignmentStatus.Accepted
                      && ParcelStatusWorkflow.CanTransition(parcel.Status, ParcelStatusCode.OutForDelivery),
            CanFail: inProgress && !codCollected,
            CanComplete: inProgress && hasProof && (!parcel.IsCod || codCollected),
            CanReturnToWarehouse: returnPending);
    }
}
