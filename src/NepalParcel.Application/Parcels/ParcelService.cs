using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Mapping;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Application.Parcels;

internal sealed partial class ParcelService(
    IParcelRepository parcels,
    IPaymentRepository payments,
    IBranchRepository branches,
    IWarehouseRepository warehouses,
    ICustomerRepository customers,
    IAuditLogRepository auditLogs,
    PriceEngine priceEngine,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IOptions<BookingOptions> options,
    IValidator<BookParcelRequest> bookValidator,
    IValidator<ParcelSearchQuery> searchValidator,
    IValidator<CancelParcelRequest> cancelValidator,
    IValidator<ConfirmPaymentRequest> confirmValidator,
    ILogger<ParcelService> logger) : IParcelService
{
    private const int TrackingNumberAttempts = 5;

    private bool IsCounterStaff => Roles.CounterStaff.Any(currentUser.IsInRole);

    private bool IsParcelViewer => Roles.ParcelViewers.Any(currentUser.IsInRole);

    public async Task<ParcelResponse> BookAsync(BookParcelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await bookValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var userId = currentUser.UserId ?? throw new UnauthorizedException("Authentication is required.");
        var counter = IsCounterStaff;
        var now = clock.GetUtcNow();

        var origin = await branches.GetByIdAsync(request.OriginBranchId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Branch", request.OriginBranchId);
        if (!origin.IsActive)
        {
            throw new DomainException("The selected branch is not accepting parcels.");
        }

        var serviceType = Enum.Parse<ServiceType>(request.ServiceType, ignoreCase: true);
        var method = Enum.Parse<PaymentMethod>(request.PaymentMethod, ignoreCase: true);
        var senderAddress = request.Sender.Address.ToDomain();
        var receiverAddress = request.Receiver.Address.ToDomain();

        // The charge is always calculated here. The request carries no price and none would be trusted.
        var price = await priceEngine
            .CalculateAsync(
                origin,
                receiverAddress,
                serviceType,
                request.WeightKg,
                request.LengthCm,
                request.WidthCm,
                request.HeightCm,
                request.CodAmount ?? 0m,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        var customer = counter
            ? await FindOrCreateWalkInAsync(request.Sender, senderAddress, cancellationToken).ConfigureAwait(false)
            : await GetOrCreateOnlineCustomerAsync(userId, request.Sender, senderAddress, cancellationToken).ConfigureAwait(false);

        var parcel = Parcel.Book(
            new NewParcelDetails
            {
                TrackingNumber = await NewTrackingNumberAsync(cancellationToken).ConfigureAwait(false),
                SenderCustomerId = customer.Id,
                SenderName = request.Sender.FullName,
                SenderPhone = request.Sender.PhoneNumber,
                SenderAddress = senderAddress,
                SenderAddressInstructions = request.Sender.AddressInstructions,
                ReceiverName = request.Receiver.FullName,
                ReceiverPhone = request.Receiver.PhoneNumber,
                ReceiverAddress = receiverAddress,
                ReceiverAddressInstructions = request.Receiver.AddressInstructions,
                OriginBranchId = origin.Id,
                DestinationBranchId = price.DestinationArea.ServingBranchId,
                ServiceType = serviceType,
                Channel = counter ? BookingChannel.Counter : BookingChannel.Online,
                Description = request.Description,
                Quantity = request.Quantity,
                WeightKg = request.WeightKg,
                LengthCm = request.LengthCm,
                WidthCm = request.WidthCm,
                HeightCm = request.HeightCm,
                DeclaredValue = request.DeclaredValue ?? 0m,
                PaymentMethod = method,
                Price = price.Breakdown,
                CodGoodsAmount = request.CodAmount ?? 0m,
                PricingRuleId = price.Rule.Id,
                Notes = request.Notes,
            },
            now,
            userId,
            currentUser.UserName);
        parcel.SetEstimatedDelivery(price.EstimatedDeliveryAtUtc);
        parcels.Add(parcel);

        Payment? payment = null;
        if (parcel.DeliveryCharge > 0)
        {
            payment = Payment.Create(parcel.Id, parcel.DeliveryCharge, method, customer.Id);
            if (counter && method != PaymentMethod.CashOnDelivery)
            {
                // Counter staff take the money (or check the wallet transfer) while the customer is standing there.
                payment.MarkPaid(now, request.PaymentReference);
            }

            payments.Add(payment);
        }

        AddAudit(
            AuditActions.ParcelBooked,
            parcel.Id,
            now,
            new { parcel.TrackingNumber, channel = parcel.Channel.ToString(), service = parcel.ServiceType.ToString(), charge = parcel.DeliveryCharge });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogBooked(logger, parcel.Id, parcel.TrackingNumber, parcel.Channel);
        return await ToResponseAsync(parcel, payment, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PagedResponse<ParcelSummaryResponse>> SearchAsync(ParcelSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await searchValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        // Staff see every parcel; customers only their own (a customer without a profile sees nothing).
        Guid? ownerId = null;
        if (!IsParcelViewer)
        {
            ownerId = (await GetCallerCustomerAsync(cancellationToken).ConfigureAwait(false))?.Id ?? Guid.Empty;
        }

        var filter = new ParcelFilter(
            ownerId,
            string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
            ParseOrNull<ParcelStatusCode>(query.Status),
            ParseOrNull<ServiceType>(query.ServiceType),
            ParseOrNull<PaymentMethod>(query.PaymentMethod),
            ParseOrNull<BookingChannel>(query.Channel),
            query.IsCod,
            query.OriginBranchId,
            query.BookedFrom is { } from ? StartOfNepalDay(from) : null,
            query.BookedTo is { } to ? StartOfNepalDay(to.AddDays(1)) : null);

        var (items, total) = await parcels.SearchAsync(filter, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return new PagedResponse<ParcelSummaryResponse>(items.Select(i => i.ToResponse()).ToList(), query.Page, query.PageSize, total);
    }

    public async Task<ParcelResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var parcel = await parcels.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", id);
        await EnsureCanViewAsync(parcel, cancellationToken).ConfigureAwait(false);

        return await ToResponseAsync(parcel, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ParcelResponse> GetByTrackingNumberAsync(string trackingNumber, CancellationToken cancellationToken = default)
    {
        var normalized = TrackingNumbers.Normalize(trackingNumber);
        var parcel = TrackingNumbers.IsValid(normalized)
            ? await parcels.GetByTrackingNumberAsync(normalized, cancellationToken).ConfigureAwait(false)
            : null;
        if (parcel is null)
        {
            throw new NotFoundException("Parcel", normalized);
        }

        await EnsureCanViewAsync(parcel, cancellationToken).ConfigureAwait(false);
        return await ToResponseAsync(parcel, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ParcelResponse> CancelAsync(Guid id, CancelParcelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await cancelValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var parcel = await parcels.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", id);
        await EnsureCanViewAsync(parcel, cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        parcel.Cancel(request.Reason, IsCounterStaff, now, currentUser.UserId, currentUser.UserName);

        // A charge that is not yet paid can no longer be paid; money already taken is flagged as refunded.
        var payment = await payments.GetLatestByParcelIdAsync(parcel.Id, cancellationToken).ConfigureAwait(false);
        if (payment is not null)
        {
            if (payment.Status == PaymentStatus.Pending)
            {
                payment.MarkFailed();
            }
            else if (payment.Status == PaymentStatus.Paid)
            {
                payment.MarkRefunded(partial: false);
            }
        }

        AddAudit(AuditActions.ParcelCancelled, parcel.Id, now, new { parcel.TrackingNumber, byStaff = IsCounterStaff });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogCancelled(logger, parcel.Id, parcel.TrackingNumber);
        return await ToResponseAsync(parcel, payment, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ParcelResponse> ConfirmPaymentAsync(Guid id, ConfirmPaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await confirmValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var parcel = await parcels.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", id);

        if (parcel.Status == ParcelStatusCode.Cancelled)
        {
            throw new DomainException("A cancelled booking cannot be paid.");
        }

        if (parcel.PaymentMethod == PaymentMethod.CashOnDelivery)
        {
            throw new DomainException("Cash-on-delivery charges are collected by the rider at delivery.");
        }

        var payment = await payments.GetLatestByParcelIdAsync(parcel.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Payment for parcel", id);
        if (payment.Status != PaymentStatus.Pending)
        {
            throw new ConflictException($"This payment is already '{payment.Status}'.");
        }

        var now = clock.GetUtcNow();
        payment.MarkPaid(now, request.Reference);
        AddAudit(AuditActions.PaymentConfirmed, parcel.Id, now, new { parcel.TrackingNumber, method = payment.Method.ToString() });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogPaymentConfirmed(logger, parcel.Id);
        return await ToResponseAsync(parcel, payment, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReceiptResponse> GetReceiptAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var parcel = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        var receipt = options.Value.Receipt;

        return new ReceiptResponse(
            $"R-{parcel.TrackingNumber}",
            receipt.CompanyName,
            receipt.CompanyPhone,
            receipt.CompanyTaxNumber,
            parcel);
    }

    private async Task<ParcelResponse> ToResponseAsync(Parcel parcel, Payment? payment, CancellationToken cancellationToken)
    {
        payment ??= await payments.GetLatestByParcelIdAsync(parcel.Id, cancellationToken).ConfigureAwait(false);

        var branchIds = parcel.StatusHistory.Select(h => h.BranchId)
            .Append(parcel.OriginBranchId)
            .Append(parcel.DestinationBranchId)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var warehouseIds = parcel.StatusHistory.Select(h => h.WarehouseId).OfType<Guid>().Distinct().ToList();

        var found = await branches.GetByIdsAsync(branchIds, cancellationToken).ConfigureAwait(false);
        var origin = found.TryGetValue(parcel.OriginBranchId, out var o) ? o : throw new NotFoundException("Branch", parcel.OriginBranchId);
        Branch? destination = parcel.DestinationBranchId is { } d && found.TryGetValue(d, out var db) ? db : null;

        var locations = found.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        if (warehouseIds.Count > 0)
        {
            foreach (var (id, warehouse) in await warehouses.GetByIdsAsync(warehouseIds, cancellationToken).ConfigureAwait(false))
            {
                locations[id] = warehouse.Name;
            }
        }

        var canCancel = IsCounterStaff
            ? parcel.CanBeCancelled(byStaff: true)
            : !IsParcelViewer && parcel.CanBeCancelled(byStaff: false);

        // Only staff see who made a change, and only staff are offered status changes (and only ones their role may make).
        var allowedNext = IsParcelViewer || Roles.All.Any(currentUser.IsInRole)
            ? parcel.AllowedNextStatuses()
                .Where(s => s != ParcelStatusCode.Cancelled && StatusAccess.CanSet(currentUser, s))
                .Select(s => s.ToString())
                .ToList()
            : [];

        return parcel.ToResponse(payment, origin, destination, canCancel, locations, IsParcelViewer, allowedNext);
    }

    private async Task EnsureCanViewAsync(Parcel parcel, CancellationToken cancellationToken)
    {
        if (IsParcelViewer)
        {
            return;
        }

        // Customers only ever see their own parcels. Anything else looks exactly like "does not exist".
        var customer = await GetCallerCustomerAsync(cancellationToken).ConfigureAwait(false);
        if (customer is null || customer.Id != parcel.SenderCustomerId)
        {
            throw new NotFoundException("Parcel", parcel.Id);
        }
    }

    private async Task<Customer?> GetCallerCustomerAsync(CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId
            ? await customers.GetByUserIdAsync(userId, cancellationToken).ConfigureAwait(false)
            : null;

    private async Task<Customer> GetOrCreateOnlineCustomerAsync(Guid userId, BookingPartyDto sender, NepalAddress address, CancellationToken cancellationToken)
    {
        var existing = await customers.GetByUserIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        // Normally created at registration; this covers accounts that were created some other way.
        var created = Customer.Create(sender.FullName, sender.PhoneNumber, userId: userId, address: address);
        customers.Add(created);
        return created;
    }

    private async Task<Customer> FindOrCreateWalkInAsync(BookingPartyDto sender, NepalAddress address, CancellationToken cancellationToken)
    {
        NepalPhoneNumber.TryNormalize(sender.PhoneNumber, out var phone);
        var name = sender.FullName.Trim();

        var existing = await customers.FindWalkInAsync(phone, name, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var created = Customer.Create(name, phone, address: address);
        customers.Add(created);
        return created;
    }

    private async Task<string> NewTrackingNumberAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < TrackingNumberAttempts; attempt++)
        {
            var candidate = TrackingNumbers.Generate();
            if (!await parcels.TrackingNumberExistsAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw new ConflictException("Could not allocate a tracking number. Please try again.");
    }

    private static TEnum? ParseOrNull<TEnum>(string? value)
        where TEnum : struct, Enum =>
        string.IsNullOrWhiteSpace(value) ? null : Enum.Parse<TEnum>(value, ignoreCase: true);

    /// <summary>Midnight at the start of a Nepal calendar day, as a UTC instant.</summary>
    private static DateTimeOffset StartOfNepalDay(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), NepalTime.Offset).ToUniversalTime();

    private void AddAudit(string action, Guid entityId, DateTimeOffset now, object? newValues) =>
        auditLogs.Add(AuditLog.Create(
            action,
            "Parcel",
            entityId.ToString(),
            currentUser.UserId,
            now,
            newValues: newValues is null ? null : JsonSerializer.Serialize(newValues),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));

    [LoggerMessage(EventId = 7001, Level = LogLevel.Information, Message = "Parcel {ParcelId} booked with tracking number {TrackingNumber} via {Channel}")]
    private static partial void LogBooked(ILogger logger, Guid parcelId, string trackingNumber, BookingChannel channel);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Information, Message = "Parcel {ParcelId} ({TrackingNumber}) cancelled")]
    private static partial void LogCancelled(ILogger logger, Guid parcelId, string trackingNumber);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Information, Message = "Payment for parcel {ParcelId} confirmed")]
    private static partial void LogPaymentConfirmed(ILogger logger, Guid parcelId);
}
