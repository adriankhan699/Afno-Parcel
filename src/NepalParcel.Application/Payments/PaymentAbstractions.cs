using System.ComponentModel.DataAnnotations;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Payments;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Application.Payments;

/// <summary>Payment settings (configuration section <c>Payments</c>). Not secrets.</summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Payments";
    public const string TestMode = "Test";
    public const string LiveMode = "Live";

    /// <summary>
    /// <c>Test</c> (the default) uses simulated eSewa and Khalti providers that live inside this application, so the whole
    /// payment flow can be tried without any provider account. <c>Live</c> refuses online payments until real gateways have
    /// been added (see docs/payments.md): no real provider is called by this build.
    /// </summary>
    [Required]
    [RegularExpression("^(Test|Live)$", ErrorMessage = "Payments:Mode must be Test or Live.")]
    public string Mode { get; set; } = TestMode;

    /// <summary>How long an online attempt can stay unconfirmed before it is given up.</summary>
    [Range(1, 1440)]
    public int TransactionLifetimeMinutes { get; set; } = 30;

    /// <summary>The name shown on the simulated provider page.</summary>
    [Required]
    [StringLength(100)]
    public string TestMerchantName { get; set; } = "Afno Parcel (test merchant)";

    public bool IsTest => string.Equals(Mode, TestMode, StringComparison.Ordinal);
}

// ---- the gateway abstraction -----------------------------------------------------------------------------------------

public sealed record GatewayInitiateRequest(string MerchantReference, decimal Amount, string Currency, string Description, string ReturnUrl);

/// <summary>Where to send the customer to pay, and the provider's own id for the attempt when it gives one.</summary>
public sealed record GatewayInitiation(string RedirectUrl, string? ProviderSessionId);

/// <param name="MerchantReference">Our reference for the attempt.</param>
/// <param name="ExpectedAmount">What we expect the provider to report. Passed so the gateway can ask the provider the right question; the service checks the answer itself.</param>
/// <param name="ExpectedCurrency">The currency we expect.</param>
/// <param name="CallbackData">Whatever the customer's browser brought back. Untrusted: it may help to find the payment at the provider, nothing more.</param>
public sealed record GatewayVerifyRequest(
    string MerchantReference,
    decimal ExpectedAmount,
    string ExpectedCurrency,
    IReadOnlyDictionary<string, string> CallbackData);

public enum GatewayState
{
    /// <summary>The provider has no completed payment yet (still being paid, or never paid).</summary>
    Pending = 1,

    /// <summary>The provider says the money was taken.</summary>
    Completed = 2,

    Failed = 3,
    Cancelled = 4,
}

/// <summary>
/// What the provider itself says about an attempt. The service never takes <see cref="State"/> on trust alone: for a completed
/// payment it also checks the merchant reference, the amount and the currency against what we recorded.
/// </summary>
public sealed record GatewayVerification(
    GatewayState State,
    string? MerchantReference,
    decimal? Amount,
    string? Currency,
    string? ProviderTransactionId,
    string? FailureReason);

/// <summary>
/// One online payment provider. Implementations call the provider's server-side API to verify a payment. The simulated
/// providers used in test mode are <see cref="TestPaymentGateway"/>; a real provider is added by implementing this interface
/// and registering it. No gateway may report a payment as completed because of anything the browser said.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>The provider's name as stored on transactions: <c>Esewa</c> or <c>Khalti</c>.</summary>
    string Provider { get; }

    /// <summary>True for the simulated providers.</summary>
    bool IsTest { get; }

    /// <summary>
    /// Opens the attempt at the provider. Asking again for a reference that is already open returns the same redirect:
    /// initiation can be repeated safely.
    /// </summary>
    Task<GatewayInitiation> InitiateAsync(GatewayInitiateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Asks the provider (server to server) what happened to the attempt.</summary>
    Task<GatewayVerification> VerifyAsync(GatewayVerifyRequest request, CancellationToken cancellationToken = default);
}

public interface IPaymentGatewayRegistry
{
    /// <summary>The gateway for the provider; refuses (422) when none is available in the current mode.</summary>
    IPaymentGateway Get(string provider);

    bool IsTestMode { get; }
}

/// <summary>The simulated provider's books (test mode only).</summary>
public interface ITestGatewaySessions
{
    void Add(TestGatewaySession session);

    /// <summary>Tracked, so it can be completed.</summary>
    Task<TestGatewaySession?> GetByReferenceAsync(string merchantReference, CancellationToken cancellationToken = default);
}

public interface ITestGatewayService
{
    Task<TestGatewaySessionResponse> GetSessionAsync(string reference, CancellationToken cancellationToken = default);

    Task<TestGatewaySessionResponse> CompleteAsync(string reference, TestGatewayCompleteRequest request, CancellationToken cancellationToken = default);
}

// ---- repositories ------------------------------------------------------------------------------------------------------

public sealed record TransactionFilter(
    string? Provider = null,
    TransactionStatus? Status = null,
    Guid? ParcelId = null,
    Guid? BranchId = null,
    string? Search = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? BeforeUtc = null);

public sealed record TransactionListItem(PaymentTransaction Transaction, Guid ParcelId, string TrackingNumber);

public interface IPaymentTransactionRepository
{
    void Add(PaymentTransaction transaction);

    /// <summary>Tracked, so a verification can complete it.</summary>
    Task<PaymentTransaction?> GetByMerchantReferenceAsync(string merchantReference, CancellationToken cancellationToken = default);

    /// <summary>The payment's attempts that are still pending (tracked).</summary>
    Task<IReadOnlyList<PaymentTransaction>> GetPendingForPaymentAsync(Guid paymentId, CancellationToken cancellationToken = default);

    /// <summary>Whether another transaction of the provider already carries this provider reference.</summary>
    Task<bool> ProviderReferenceUsedAsync(string provider, string providerReference, Guid exceptTransactionId, CancellationToken cancellationToken = default);

    /// <summary>Oldest first.</summary>
    Task<IReadOnlyList<PaymentTransaction>> GetByPaymentsAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<(IReadOnlyList<TransactionListItem> Items, int TotalCount)> SearchAsync(TransactionFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);
}

public sealed record PaymentReportRow(PaymentMethod Method, PaymentStatus Status, decimal Amount, DateTimeOffset CreatedAtUtc, DateTimeOffset? PaidAtUtc);

public sealed record TransactionStatRow(string Provider, TransactionStatus Status, TransactionType Type, decimal Amount);

public sealed record SettlementStatRow(SettlementStatus Status, decimal TotalAmount, decimal? CountedAmount);

/// <summary>A cash collection with what is needed to place it in the agent's cash position.</summary>
public sealed record CodRow(Guid AgentId, decimal CollectedAmount, CodCollectionStatus Status, Guid? SettlementId, DateTimeOffset CollectedAtUtc);

public interface IPaymentReportRepository
{
    /// <summary>Payments paid in the range, and payments created in the range (to count the pending ones).</summary>
    Task<IReadOnlyList<PaymentReportRow>> GetPaymentRowsAsync(DateTimeOffset fromUtc, DateTimeOffset beforeUtc, Guid? branchId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransactionStatRow>> GetTransactionStatsAsync(DateTimeOffset fromUtc, DateTimeOffset beforeUtc, Guid? branchId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SettlementStatRow>> GetSettlementStatsAsync(DateTimeOffset fromUtc, DateTimeOffset beforeUtc, Guid? branchId, CancellationToken cancellationToken = default);
}

public sealed record SettlementFilter(
    SettlementStatus? Status = null,
    Guid? AgentId = null,
    Guid? BranchId = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? BeforeUtc = null);

public interface ICodSettlementRepository
{
    void Add(CodSettlement settlement);

    /// <summary>Tracked, so it can be verified or disputed.</summary>
    Task<CodSettlement?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ReferenceExistsAsync(string referenceNumber, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<(IReadOnlyList<CodSettlement> Items, int TotalCount)> SearchAsync(SettlementFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);
}

public interface IPaymentService
{
    Task<InitiatePaymentResponse> InitiateAsync(InitiatePaymentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The customer's browser came back from the provider. The server asks the provider what really happened, checks the
    /// reference, amount and currency, and only then decides. Calling it again for a finished attempt changes nothing.
    /// </summary>
    Task<PaymentStatusResponse> VerifyAsync(string provider, PaymentCallbackRequest request, CancellationToken cancellationToken = default);

    Task<PaymentStatusResponse> GetStatusAsync(string reference, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PaymentResponse>> GetForParcelAsync(Guid parcelId, CancellationToken cancellationToken = default);

    Task<PaymentReceiptResponse> GetReceiptAsync(Guid paymentId, CancellationToken cancellationToken = default);

    Task<PaymentResponse> RecordOfficeCashAsync(RecordOfficeCashRequest request, CancellationToken cancellationToken = default);

    Task<PagedResponse<PaymentTransactionRow>> SearchTransactionsAsync(PaymentTransactionQuery query, CancellationToken cancellationToken = default);
}

public interface ISettlementService
{
    /// <summary>The signed-in agent hands in all the cash they have collected and not yet handed in.</summary>
    Task<SettlementResponse> SubmitAsync(CreateSettlementRequest request, CancellationToken cancellationToken = default);

    Task<AgentCashSummaryResponse> GetMyCashAsync(CancellationToken cancellationToken = default);

    Task<PagedResponse<SettlementResponse>> GetMineAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    Task<PagedResponse<SettlementResponse>> SearchAsync(SettlementQuery query, CancellationToken cancellationToken = default);

    Task<SettlementResponse> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SettlementResponse> VerifyAsync(Guid id, VerifySettlementRequest request, CancellationToken cancellationToken = default);

    Task<SettlementResponse> ResolveAsync(Guid id, ResolveSettlementRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentCashPositionRow>> GetAgentCashAsync(AgentCashQuery query, CancellationToken cancellationToken = default);
}

public interface IPaymentReportService
{
    Task<PaymentReportResponse> GetSummaryAsync(PaymentReportQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Statuses an assignment can show in a cash screen (kept here so the services share one definition).</summary>
internal static class CashStatuses
{
    public static string For(CodCollectionStatus status, bool inSettlement) =>
        status == CodCollectionStatus.Collected && inSettlement ? "InSettlement" : status.ToString();
}
