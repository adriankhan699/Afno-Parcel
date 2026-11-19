using System.Text;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Identity;

namespace NepalParcel.Application.Network;

/// <summary>Builds the printable label for a parcel: its tracking number as a barcode and as a QR code.</summary>
internal sealed class ParcelLabelService(
    IParcelRepository parcels,
    ICustomerRepository customers,
    IBranchRepository branches,
    ILabelGenerator generator,
    ICurrentUser currentUser,
    IOptions<TrackingOptions> options) : IParcelLabelService
{
    public async Task<ParcelLabelResponse> GetLabelAsync(Guid parcelId, CancellationToken cancellationToken = default)
    {
        var parcel = await parcels.GetByIdAsync(parcelId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", parcelId);

        if (!Roles.ParcelViewers.Any(currentUser.IsInRole))
        {
            // Senders can print the label of their own parcels. Anyone else gets the same answer as for a parcel that does not exist.
            var customer = currentUser.UserId is { } userId
                ? await customers.GetByUserIdAsync(userId, cancellationToken).ConfigureAwait(false)
                : null;
            if (customer is null || customer.Id != parcel.SenderCustomerId)
            {
                throw new NotFoundException("Parcel", parcelId);
            }
        }

        var origin = await branches.GetByIdAsync(parcel.OriginBranchId, cancellationToken).ConfigureAwait(false);
        var qrContent = $"{options.Value.PublicBaseUrl.TrimEnd('/')}/track/{parcel.TrackingNumber}";
        var address = parcel.ReceiverAddress;

        return new ParcelLabelResponse(
            parcel.TrackingNumber,
            ToDataUri(generator.CreateBarcodeSvg(parcel.TrackingNumber)),
            ToDataUri(generator.CreateQrSvg(qrContent)),
            qrContent,
            parcel.ServiceType.ToString(),
            parcel.Quantity,
            parcel.WeightKg,
            origin?.Name ?? "Afno Parcel",
            address.District,
            parcel.ReceiverName,
            $"{address.Municipality}-{address.WardNumber}, {address.District}",
            parcel.CodAmount,
            parcel.BookedAtUtc);
    }

    private static string ToDataUri(string svg) => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
}
