using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Network;
using NepalParcel.Application.Parcels;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.Application.Tracking;

/// <summary>
/// Staff changing a parcel's status by hand. Every change goes through <see cref="ParcelStatusChanger"/>, which checks the
/// workflow and appends a history row; nothing here edits or deletes history.
/// </summary>
internal sealed partial class ParcelStatusService(
    IParcelRepository parcels,
    ParcelStatusChanger changer,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IValidator<UpdateParcelStatusRequest> updateValidator,
    ILogger<ParcelStatusService> logger) : IParcelStatusService
{
    public async Task<ParcelStatusUpdateResponse> UpdateAsync(Guid parcelId, UpdateParcelStatusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await updateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var target = Enum.Parse<ParcelStatusCode>(request.Status, ignoreCase: true);
        EnsureMayChangeTo(target);

        var parcel = await parcels.GetByIdAsync(parcelId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", parcelId);
        return await ApplyAsync(parcel, target, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ParcelStatusUpdateResponse> UpdateByTrackingNumberAsync(string trackingNumber, UpdateParcelStatusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await updateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var target = Enum.Parse<ParcelStatusCode>(request.Status, ignoreCase: true);
        EnsureMayChangeTo(target);

        var parcel = TrackingNumbers.TryExtract(trackingNumber, out var normalized)
            ? await parcels.GetByTrackingNumberAsync(normalized, cancellationToken).ConfigureAwait(false)
            : null;
        return await ApplyAsync(
            parcel ?? throw new NotFoundException("Parcel", TrackingNumbers.Normalize(trackingNumber)),
            target,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ParcelStatusUpdateResponse> ApplyAsync(Parcel parcel, ParcelStatusCode target, UpdateParcelStatusRequest request, CancellationToken cancellationToken)
    {
        var change = await changer
            .ApplyAsync(parcel, target, request.Remarks, request.BranchId, request.WarehouseId, request.ToWarehouseId, cancellationToken)
            .ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogChanged(logger, parcel.Id, change.Previous, target);
        return new ParcelStatusUpdateResponse(
            parcel.Id,
            parcel.TrackingNumber,
            change.Previous.ToString(),
            ParcelStatusCatalog.Get(change.Previous).Name,
            target.ToString(),
            ParcelStatusCatalog.Get(target).Name,
            change.History.OccurredAtUtc,
            change.Location,
            parcel.AllowedNextStatuses().Where(s => s != ParcelStatusCode.Cancelled).Select(s => s.ToString()).ToList());
    }

    /// <summary>Checked before the parcel is even loaded, so a role that may not make this change learns nothing about the parcel.</summary>
    private void EnsureMayChangeTo(ParcelStatusCode target)
    {
        if (currentUser.UserId is null)
        {
            throw new UnauthorizedException("Authentication is required.");
        }

        if (!StatusAccess.CanSet(currentUser, target))
        {
            throw new ForbiddenException($"Your role is not allowed to mark a parcel as '{ParcelStatusCatalog.Get(target).Name}'.");
        }
    }

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information, Message = "Parcel {ParcelId} moved from {From} to {To}")]
    private static partial void LogChanged(ILogger logger, Guid parcelId, ParcelStatusCode from, ParcelStatusCode to);
}

/// <summary>Checks the signed-in user's roles against <see cref="ParcelStatusPermissions"/>.</summary>
internal static class StatusAccess
{
    public static bool CanSet(ICurrentUser user, ParcelStatusCode to) =>
        Roles.All.Any(role => user.IsInRole(role) && ParcelStatusPermissions.CanSet(role, to));
}
