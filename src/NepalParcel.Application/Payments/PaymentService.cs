using System.Globalization;
using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Delivery;
using NepalParcel.Application.Network;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Payments;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Application.Payments;

/// <summary>
/// Paying for a parcel. The rules that matter most:
/// <list type="bullet">
/// <item>A payment is never marked as paid because of what the customer's browser says. The server asks the provider itself.</item>
/// <item>A completed payment is accepted only if the merchant reference, the amount and the currency the provider reports match
/// what we recorded when the attempt was started; otherwise the attempt is failed and nothing is marked as paid.</item>
/// <item>Verifying the same attempt twice (a duplicate callback, a double click, a refresh) changes nothing the second time.</item>
/// <item>The provider's own transaction id is stored, and can belong to only one transaction.</item>
/// </list>
/// </summary>
internal sealed partial class PaymentService(
    IPaymentGatewayRegistry gateways,
    IPaymentRepository payments,
    IPaymentTransactionRepository transactions,
    IParcelRepository parcels,
    ICustomerRepository customers,
    PaymentLedger ledger,
    WorkplaceAccess access,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IOptions<PaymentOptions> paymentOptions,
    IOptions<BookingOptions> bookingOptions,
    IValidator<InitiatePaymentRequest> initiateValidator,
    IValidator<PaymentCallbackRequest> callbackValidator,
    IValidator<RecordOfficeCashRequest> cashValidator,
    IValidator<PaymentTransactionQuery> queryValidator,
    ILogger<PaymentService> logger) : IPaymentService
{
    private bool IsCounterStaff => Roles.CounterStaff.Any(currentUser.IsInRole);

    // ---- paying online ---------------------------------------------------------------------------------------------------

    public async Task<InitiatePaymentResponse> InitiateAsync(InitiatePaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await initiateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var parcel = await parcels.GetByIdAsync(request.ParcelId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", request.ParcelId);
        await EnsureMayActOnParcelAsync(parcel, cancellationToken).ConfigureAwait(false);

        if (parcel.Status == ParcelStatusCode.Cancelled)
        {
            throw new DomainException("A cancelled booking cannot be paid.");
        }

        var payment = await payments.GetLatestByParcelIdAsync(parcel.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Payment for parcel", parcel.Id);
        if (payment.Status != PaymentStatus.Pending)
        {
            throw new ConflictException($"This payment is already '{payment.Status}'.");
        }

        var provider = Enum.Parse<PaymentMethod>(request.Provider, ignoreCase: true);
        if (payment.Method != provider)
        {
            throw new DomainException($"This parcel is to be paid with {payment.Method}, not {provider}.");
        }

        var gateway = gateways.Get(provider.ToString());
        var now = clock.GetUtcNow();
        var lifetime = TimeSpan.FromMinutes(paymentOptions.Value.TransactionLifetimeMinutes);

        // Starting again is safe: a still-open attempt for the same provider is reused, any other open attempt is closed.
        PaymentTransaction? open = null;
        foreach (var pending in await transactions.GetPendingForPaymentAsync(payment.Id, cancellationToken).ConfigureAwait(false))
        {
            if (open is null && pending.Provider == provider.ToString() && !pending.IsExpired(now))
            {
                open = pending;
            }
            else
            {
                pending.Fail(now, pending.IsExpired(now) ? "The payment was not completed in time." : "Replaced by a newer attempt.");
            }
        }

        var transaction = open ?? PaymentTransaction.Create(
            payment.Id,
            TransactionType.Charge,
            provider.ToString(),
            payment.Amount,
            now,
            expiresAtUtc: now.Add(lifetime),
            recordedByUserId: null);
        if (open is null)
        {
            transactions.Add(transaction);
        }

        var initiation = await gateway.InitiateAsync(
            new GatewayInitiateRequest(
                transaction.MerchantReference,
                transaction.Amount,
                transaction.Currency,
                $"Delivery of parcel {parcel.TrackingNumber}",
                $"/pay/return/{provider.ToString().ToLowerInvariant()}"),
            cancellationToken).ConfigureAwait(false);

        AddAudit(AuditActions.PaymentInitiated, payment.Id, now, new { parcelId = parcel.Id, provider = provider.ToString(), reference = transaction.MerchantReference, amount = transaction.Amount, reused = open is not null });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogInitiated(logger, payment.Id, provider.ToString(), transaction.MerchantReference);
        return new InitiatePaymentResponse(
            payment.Id,
            transaction.MerchantReference,
            provider.ToString(),
            transaction.Amount,
            transaction.Currency,
            initiation.RedirectUrl,
            transaction.ExpiresAtUtc ?? now.Add(lifetime),
            gateway.IsTest);
    }

    public async Task<PaymentStatusResponse> VerifyAsync(string provider, PaymentCallbackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await callbackValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var (transaction, payment) = await LoadAttemptAsync(provider, request.Reference, cancellationToken).ConfigureAwait(false);

        // A finished attempt is final. Repeated callbacks, refreshes and replays all land here and change nothing.
        if (transaction.Status == TransactionStatus.Pending)
        {
            var now = clock.GetUtcNow();
            var gateway = gateways.Get(transaction.Provider);
            var data = (IReadOnlyDictionary<string, string>)(request.ProviderData ?? []);

            // The provider is asked directly (server to server). What the browser carried is never the answer.
            var verification = await gateway
                .VerifyAsync(new GatewayVerifyRequest(transaction.MerchantReference, transaction.Amount, transaction.Currency, data), cancellationToken)
                .ConfigureAwait(false);

            await DecideAsync(transaction, payment, verification, now, cancellationToken).ConfigureAwait(false);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyException)
            {
                // Another request finished the same attempt at the same moment. Report what it decided.
                unitOfWork.DiscardPendingChanges();
                (transaction, payment) = await LoadAttemptAsync(provider, request.Reference, cancellationToken).ConfigureAwait(false);
            }
        }

        return await ToStatusAsync(transaction, payment, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PaymentStatusResponse> GetStatusAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (!MerchantReferences.LooksValid(reference))
        {
            throw new NotFoundException("Payment", "reference");
        }

        var transaction = await transactions.GetByMerchantReferenceAsync(reference, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Payment", "reference");
        var payment = await payments.GetByIdAsync(transaction.PaymentId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Payment", "reference");
        return await ToStatusAsync(transaction, payment, cancellationToken).ConfigureAwait(false);
    }

    private async Task DecideAsync(PaymentTransaction transaction, Payment payment, GatewayVerification verification, DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch (verification.State)
        {
            case GatewayState.Completed:
                var problem = await CheckCompletedAsync(transaction, verification, cancellationToken).ConfigureAwait(false);
                if (problem is not null)
                {
                    transaction.Fail(now, $"Verification failed: {problem}.");
                    AddAudit(
                        AuditActions.PaymentVerificationMismatch,
                        payment.Id,
                        now,
                        new
                        {
                            reference = transaction.MerchantReference,
                            problem,
                            expectedAmount = transaction.Amount,
                            reportedAmount = verification.Amount,
                            reportedCurrency = verification.Currency,
                        });
                    LogMismatch(logger, payment.Id, transaction.MerchantReference, problem);
                    return;
                }

                transaction.Succeed(now, verification.ProviderTransactionId);
                if (payment.Status == PaymentStatus.Pending)
                {
                    payment.MarkPaid(now, verification.ProviderTransactionId);
                    AddAudit(AuditActions.PaymentVerified, payment.Id, now, new { reference = transaction.MerchantReference, provider = transaction.Provider, providerReference = transaction.ProviderReference, amount = transaction.Amount });
                    LogVerified(logger, payment.Id, transaction.Provider);
                }
                else
                {
                    // The charge was already paid another way (for example in cash at the counter): this is money taken twice.
                    AddAudit(AuditActions.PaymentDuplicateCharge, payment.Id, now, new { reference = transaction.MerchantReference, provider = transaction.Provider, providerReference = transaction.ProviderReference, paymentStatus = payment.Status.ToString() });
                    LogDuplicateCharge(logger, payment.Id, transaction.MerchantReference);
                }

                break;

            case GatewayState.Failed:
            case GatewayState.Cancelled:
                transaction.Fail(now, verification.FailureReason ?? "The provider reported that the payment was not completed.");
                AddAudit(AuditActions.PaymentFailed, payment.Id, now, new { reference = transaction.MerchantReference, state = verification.State.ToString() });
                break;

            default:
                // Not paid (yet). Leave it open, unless it has run out of time.
                if (transaction.IsExpired(now))
                {
                    transaction.Fail(now, "The payment was not completed in time.");
                    AddAudit(AuditActions.PaymentFailed, payment.Id, now, new { reference = transaction.MerchantReference, state = "Expired" });
                }

                break;
        }
    }

    /// <summary>Everything a "completed" answer must satisfy before any money is recorded. Returns what is wrong, or null.</summary>
    private async Task<string?> CheckCompletedAsync(PaymentTransaction transaction, GatewayVerification verification, CancellationToken cancellationToken)
    {
        if (!string.Equals(verification.MerchantReference, transaction.MerchantReference, StringComparison.Ordinal))
        {
            return "the merchant reference does not match";
        }

        if (verification.Amount != transaction.Amount)
        {
            return "the amount does not match";
        }

        if (!string.Equals(verification.Currency, transaction.Currency, StringComparison.Ordinal))
        {
            return "the currency does not match";
        }

        if (string.IsNullOrWhiteSpace(verification.ProviderTransactionId))
        {
            return "the provider gave no transaction reference";
        }

        if (await transactions.ProviderReferenceUsedAsync(transaction.Provider, verification.ProviderTransactionId, transaction.Id, cancellationToken).ConfigureAwait(false))
        {
            return "the provider's transaction reference was already used by another payment";
        }

        return null;
    }

    // ---- history, receipts, cash at the office ---------------------------------------------------------------------------

    public async Task<IReadOnlyList<PaymentResponse>> GetForParcelAsync(Guid parcelId, CancellationToken cancellationToken = default)
    {
        var parcel = await parcels.GetByIdAsync(parcelId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", parcelId);
        await EnsureMayViewParcelAsync(parcel, cancellationToken).ConfigureAwait(false);

        var list = await payments.GetByParcelIdAsync(parcelId, cancellationToken).ConfigureAwait(false);
        return await ToResponsesAsync(list, parcel.TrackingNumber, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PaymentReceiptResponse> GetReceiptAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        var payment = await payments.GetByIdAsync(paymentId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Payment", paymentId);
        var parcel = (await parcels.GetByIdsAsync([payment.ParcelId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(payment.ParcelId)
            ?? throw new NotFoundException("Payment", paymentId);
        await EnsureMayViewParcelAsync(parcel, cancellationToken).ConfigureAwait(false);

        if (payment.ReceiptNumber is null || payment.PaidAtUtc is null)
        {
            throw new DomainException("A receipt is only available once the payment has been paid.");
        }

        var paid = (await transactions.GetByPaymentsAsync([payment.Id], cancellationToken).ConfigureAwait(false))
            .Where(t => t.Type == TransactionType.Charge && t.Status == TransactionStatus.Succeeded)
            .OrderByDescending(t => t.CompletedAtUtc)
            .FirstOrDefault();
        var provider = paid?.Provider ?? payment.Method.ToString();
        var header = bookingOptions.Value.Receipt;

        return new PaymentReceiptResponse(
            payment.ReceiptNumber,
            header.CompanyName,
            header.CompanyPhone,
            header.CompanyTaxNumber,
            parcel.TrackingNumber,
            parcel.SenderName,
            $"Delivery charge for parcel {parcel.TrackingNumber}",
            payment.Amount,
            payment.Currency,
            payment.Method.ToString(),
            provider,
            paid?.ProviderReference,
            payment.PaidAtUtc.Value,
            provider switch
            {
                nameof(PaymentMethod.Cash) => "Branch counter",
                "CashOnDelivery" => "Delivery agent",
                _ => null,
            });
    }

    public async Task<PaymentResponse> RecordOfficeCashAsync(RecordOfficeCashRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await cashValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var userId = currentUser.UserId ?? throw new UnauthorizedException("Authentication is required.");
        var parcel = await parcels.GetByIdAsync(request.ParcelId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", request.ParcelId);
        if (parcel.Status == ParcelStatusCode.Cancelled)
        {
            throw new DomainException("A cancelled booking cannot be paid.");
        }

        var payment = await payments.GetLatestByParcelIdAsync(parcel.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Payment for parcel", parcel.Id);
        if (payment.Method != PaymentMethod.Cash)
        {
            throw new DomainException($"This parcel is to be paid with {payment.Method}, not cash at the office.");
        }

        if (payment.Status != PaymentStatus.Pending)
        {
            throw new ConflictException($"This payment is already '{payment.Status}'.");
        }

        // Exactly the charge: change is given at the counter, not recorded as a different amount.
        if (request.AmountReceived != payment.Amount)
        {
            throw new DomainException(
                string.Create(CultureInfo.InvariantCulture, $"The charge is exactly NPR {payment.Amount:0.00}. You entered NPR {request.AmountReceived:0.00}."));
        }

        var now = clock.GetUtcNow();
        payment.MarkPaid(now, string.IsNullOrWhiteSpace(request.Note) ? $"CASH {parcel.TrackingNumber}" : request.Note.Trim());
        ledger.RecordManualSuccess(payment, nameof(PaymentMethod.Cash), now, userId);
        AddAudit(AuditActions.OfficeCashRecorded, payment.Id, now, new { parcelId = parcel.Id, amount = payment.Amount });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogOfficeCash(logger, payment.Id, userId);
        return (await ToResponsesAsync([payment], parcel.TrackingNumber, cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<PagedResponse<PaymentTransactionRow>> SearchTransactionsAsync(PaymentTransactionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await queryValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var branchId = await access.ScopeFinanceBranchAsync(query.BranchId, cancellationToken).ConfigureAwait(false);
        var filter = new TransactionFilter(
            string.IsNullOrWhiteSpace(query.Provider) ? null : Enum.Parse<PaymentMethod>(query.Provider, ignoreCase: true).ToString(),
            string.IsNullOrWhiteSpace(query.Status) ? null : Enum.Parse<TransactionStatus>(query.Status, ignoreCase: true),
            query.ParcelId,
            branchId,
            string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
            query.From is { } from ? NepalDay.StartUtc(from) : null,
            query.To is { } to ? NepalDay.StartUtc(to.AddDays(1)) : null);

        var (items, total) = await transactions.SearchAsync(filter, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return new PagedResponse<PaymentTransactionRow>(
            items.Select(i => new PaymentTransactionRow(
                i.Transaction.Id,
                i.Transaction.PaymentId,
                i.ParcelId,
                i.TrackingNumber,
                i.Transaction.MerchantReference,
                i.Transaction.Type.ToString(),
                i.Transaction.Status.ToString(),
                i.Transaction.Provider,
                i.Transaction.ProviderReference,
                i.Transaction.Amount,
                i.Transaction.Currency,
                i.Transaction.FailureReason,
                i.Transaction.CreatedAtUtc,
                i.Transaction.CompletedAtUtc)).ToList(),
            query.Page,
            query.PageSize,
            total);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------

    /// <summary>The attempt for a reference and provider. Anything that does not match looks exactly like "not found".</summary>
    private async Task<(PaymentTransaction Transaction, Payment Payment)> LoadAttemptAsync(string provider, string reference, CancellationToken cancellationToken)
    {
        var transaction = await transactions.GetByMerchantReferenceAsync(reference, cancellationToken).ConfigureAwait(false);
        if (transaction is null || !string.Equals(transaction.Provider, provider, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotFoundException("Payment", "reference");
        }

        var payment = await payments.GetByIdAsync(transaction.PaymentId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Payment", "reference");
        return (transaction, payment);
    }

    private async Task<PaymentStatusResponse> ToStatusAsync(PaymentTransaction transaction, Payment payment, CancellationToken cancellationToken)
    {
        var parcel = (await parcels.GetByIdsAsync([payment.ParcelId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(payment.ParcelId);
        return new PaymentStatusResponse(
            transaction.MerchantReference,
            transaction.Provider,
            transaction.Status.ToString(),
            transaction.Amount,
            transaction.Currency,
            parcel?.TrackingNumber ?? string.Empty,
            payment.Status.ToString(),
            transaction.FailureReason,
            transaction.CompletedAtUtc);
    }

    private async Task<IReadOnlyList<PaymentResponse>> ToResponsesAsync(IReadOnlyList<Payment> list, string trackingNumber, CancellationToken cancellationToken)
    {
        if (list.Count == 0)
        {
            return [];
        }

        var all = await transactions.GetByPaymentsAsync(list.Select(p => p.Id).ToList(), cancellationToken).ConfigureAwait(false);
        return list
            .Select(p => new PaymentResponse(
                p.Id,
                p.ParcelId,
                trackingNumber,
                p.Amount,
                p.Currency,
                p.Method.ToString(),
                p.Status.ToString(),
                p.PaidAtUtc,
                p.Reference,
                p.ReceiptNumber,
                all.Where(t => t.PaymentId == p.Id)
                    .OrderBy(t => t.CreatedAtUtc)
                    .Select(t => new PaymentTransactionResponse(
                        t.Id,
                        t.MerchantReference,
                        t.Type.ToString(),
                        t.Status.ToString(),
                        t.Provider,
                        t.ProviderReference,
                        t.Amount,
                        t.Currency,
                        t.FailureReason,
                        t.CreatedAtUtc,
                        t.CompletedAtUtc))
                    .ToList()))
            .ToList();
    }

    /// <summary>Customers act only on their own parcels (anything else looks like it does not exist); counter staff on any.</summary>
    private async Task EnsureMayActOnParcelAsync(Parcel parcel, CancellationToken cancellationToken)
    {
        if (IsCounterStaff)
        {
            return;
        }

        await EnsureOwnerAsync(parcel, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureMayViewParcelAsync(Parcel parcel, CancellationToken cancellationToken)
    {
        if (Roles.ParcelViewers.Any(currentUser.IsInRole))
        {
            return;
        }

        await EnsureOwnerAsync(parcel, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureOwnerAsync(Parcel parcel, CancellationToken cancellationToken)
    {
        var customer = currentUser.UserId is { } userId
            ? await customers.GetByUserIdAsync(userId, cancellationToken).ConfigureAwait(false)
            : null;
        if (customer is null || customer.Id != parcel.SenderCustomerId)
        {
            throw new NotFoundException("Parcel", parcel.Id);
        }
    }

    private void AddAudit(string action, Guid paymentId, DateTimeOffset now, object values) =>
        auditLogs.Add(AuditLog.Create(
            action,
            "Payment",
            paymentId.ToString(),
            currentUser.UserId,
            now,
            newValues: JsonSerializer.Serialize(values),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));

    [LoggerMessage(EventId = 9501, Level = LogLevel.Information, Message = "Payment {PaymentId}: {Provider} attempt {Reference} started")]
    private static partial void LogInitiated(ILogger logger, Guid paymentId, string provider, string reference);

    [LoggerMessage(EventId = 9502, Level = LogLevel.Information, Message = "Payment {PaymentId}: verified with {Provider} and marked paid")]
    private static partial void LogVerified(ILogger logger, Guid paymentId, string provider);

    [LoggerMessage(EventId = 9503, Level = LogLevel.Warning, Message = "Payment {PaymentId}: attempt {Reference} refused, {Problem}")]
    private static partial void LogMismatch(ILogger logger, Guid paymentId, string reference, string problem);

    [LoggerMessage(EventId = 9504, Level = LogLevel.Warning, Message = "Payment {PaymentId}: attempt {Reference} completed but the charge was already paid")]
    private static partial void LogDuplicateCharge(ILogger logger, Guid paymentId, string reference);

    [LoggerMessage(EventId = 9505, Level = LogLevel.Information, Message = "Payment {PaymentId}: cash taken at the office, recorded by {UserId}")]
    private static partial void LogOfficeCash(ILogger logger, Guid paymentId, Guid userId);
}
