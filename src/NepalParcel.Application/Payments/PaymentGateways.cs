using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Application.Payments;

/// <summary>
/// A <b>simulated</b> payment provider for test mode. It behaves like an independent third party: it has its own books
/// (<see cref="TestGatewaySession"/>) that only change when the tester presses a button on the simulated provider page, and
/// verification reads those books, never anything the browser sent. It does <b>not</b> follow the real eSewa or Khalti request
/// formats: those are added later as separate <see cref="IPaymentGateway"/> classes.
/// </summary>
internal sealed class TestPaymentGateway(string provider, ITestGatewaySessions sessions, TimeProvider clock, IOptions<NepalParcel.Application.Tracking.TrackingOptions> tracking)
    : IPaymentGateway
{
    public string Provider { get; } = provider;

    public bool IsTest => true;

    public async Task<GatewayInitiation> InitiateAsync(GatewayInitiateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The same reference opens the same attempt: repeating initiation never creates a second one.
        var existing = await sessions.GetByReferenceAsync(request.MerchantReference, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            sessions.Add(TestGatewaySession.Create(Provider, request.MerchantReference, request.Amount, clock.GetUtcNow()));
        }

        var baseUrl = tracking.Value.PublicBaseUrl.TrimEnd('/');
        return new GatewayInitiation($"{baseUrl}/pay/test/{Provider.ToLowerInvariant()}/{request.MerchantReference}", request.MerchantReference);
    }

    public async Task<GatewayVerification> VerifyAsync(GatewayVerifyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await sessions.GetByReferenceAsync(request.MerchantReference, cancellationToken).ConfigureAwait(false);
        if (session is null || !string.Equals(session.Provider, Provider, StringComparison.Ordinal))
        {
            return new GatewayVerification(GatewayState.Pending, null, null, null, null, null);
        }

        var state = session.Status switch
        {
            TestSessionStatus.Paid => GatewayState.Completed,
            TestSessionStatus.Failed => GatewayState.Failed,
            TestSessionStatus.Cancelled => GatewayState.Cancelled,
            _ => GatewayState.Pending,
        };

        return new GatewayVerification(
            state,
            session.MerchantReference,
            session.Amount,
            session.Currency,
            session.ProviderTransactionId,
            state switch
            {
                GatewayState.Failed => "The payment failed at the provider.",
                GatewayState.Cancelled => "The customer cancelled the payment at the provider.",
                _ => null,
            });
    }
}

/// <summary>
/// Finds the gateway for a provider. In test mode the simulated eSewa and Khalti are available; in live mode only the real
/// gateways that have been registered are (none in this build), so a live system refuses online payments rather than pretend.
/// </summary>
internal sealed class PaymentGatewayRegistry : IPaymentGatewayRegistry
{
    private readonly ReadOnlyDictionary<string, IPaymentGateway> _gateways;

    public PaymentGatewayRegistry(
        IOptions<PaymentOptions> options,
        IEnumerable<IPaymentGateway> registered,
        ITestGatewaySessions sessions,
        TimeProvider clock,
        IOptions<NepalParcel.Application.Tracking.TrackingOptions> tracking)
    {
        IsTestMode = options.Value.IsTest;
        var map = new Dictionary<string, IPaymentGateway>(StringComparer.OrdinalIgnoreCase);
        foreach (var gateway in registered)
        {
            map[gateway.Provider] = gateway;
        }

        if (IsTestMode)
        {
            foreach (var provider in new[] { nameof(PaymentMethod.Esewa), nameof(PaymentMethod.Khalti) })
            {
                map[provider] = new TestPaymentGateway(provider, sessions, clock, tracking);
            }
        }

        _gateways = map.AsReadOnly();
    }

    public bool IsTestMode { get; }

    public IPaymentGateway Get(string provider) =>
        _gateways.TryGetValue(provider, out var gateway)
            ? gateway
            : throw new DomainException($"Online payment with {provider} is not available right now.");
}

/// <summary>
/// Writes the ledger rows for money that a person took (cash at the office, cash on delivery, a wallet transfer checked at the
/// counter). These are complete as soon as they are recorded, so there is nothing to verify later.
/// </summary>
internal sealed class PaymentLedger(IPaymentTransactionRepository transactions)
{
    public PaymentTransaction RecordManualSuccess(Payment payment, string provider, DateTimeOffset nowUtc, Guid? recordedByUserId)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var transaction = PaymentTransaction.Create(payment.Id, TransactionType.Charge, provider, payment.Amount, nowUtc, recordedByUserId: recordedByUserId);
        transaction.Succeed(nowUtc);
        transactions.Add(transaction);
        return transaction;
    }
}

internal static class TestGatewayIds
{
    /// <summary>What the simulated provider calls a payment once it has been paid, such as <c>TEST-9F3A7C21B4D8E605</c>.</summary>
    public static string NewTransactionId() => "TEST-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
}
