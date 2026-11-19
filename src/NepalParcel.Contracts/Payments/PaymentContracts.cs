namespace NepalParcel.Contracts.Payments;

// ---- paying online (eSewa, Khalti) ---------------------------------------------------------------------------------

/// <summary>Starts an online payment of a parcel's delivery charge. Provider is Esewa or Khalti and must match how the parcel was booked.</summary>
public sealed record InitiatePaymentRequest(Guid ParcelId, string Provider);

/// <summary>
/// What the website needs to send the customer to the provider. <c>Reference</c> is our own merchant reference for this attempt.
/// In test mode <c>RedirectUrl</c> points to the simulated provider page of this website and <c>TestMode</c> is true.
/// </summary>
public sealed record InitiatePaymentResponse(
    Guid PaymentId,
    string Reference,
    string Provider,
    decimal Amount,
    string Currency,
    string RedirectUrl,
    DateTimeOffset ExpiresAtUtc,
    bool TestMode);

/// <summary>
/// What the customer's browser brings back from the provider. It is never trusted: only the reference is used, and the server
/// asks the provider itself whether the payment really happened. <c>ProviderData</c> carries any provider-specific values.
/// </summary>
public sealed record PaymentCallbackRequest(string Reference, Dictionary<string, string>? ProviderData = null);

/// <summary>Where a payment attempt stands, as decided by the server after asking the provider.</summary>
public sealed record PaymentStatusResponse(
    string Reference,
    string Provider,
    string Status,
    decimal Amount,
    string Currency,
    string TrackingNumber,
    string PaymentStatus,
    string? FailureReason,
    DateTimeOffset? CompletedAtUtc);

/// <summary>The simulated provider's view of an attempt (test mode only).</summary>
public sealed record TestGatewaySessionResponse(
    string Reference,
    string Provider,
    string MerchantName,
    decimal Amount,
    string Currency,
    string Status,
    string? ProviderTransactionId);

/// <summary>What the tester does on the simulated provider page: Success, Failure or Cancel.</summary>
public sealed record TestGatewayCompleteRequest(string Outcome);

// ---- history, receipts, cash at the office --------------------------------------------------------------------------

/// <summary>One attempt or movement of money against a payment. Provider references come from the provider after verification.</summary>
public sealed record PaymentTransactionResponse(
    Guid Id,
    string Reference,
    string Type,
    string Status,
    string Provider,
    string? ProviderReference,
    decimal Amount,
    string Currency,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>A payment with its full transaction history.</summary>
public sealed record PaymentResponse(
    Guid Id,
    Guid ParcelId,
    string TrackingNumber,
    decimal Amount,
    string Currency,
    string Method,
    string Status,
    DateTimeOffset? PaidAtUtc,
    string? Reference,
    string? ReceiptNumber,
    IReadOnlyList<PaymentTransactionResponse> Transactions);

/// <summary>A printable receipt for a payment that has been paid.</summary>
public sealed record PaymentReceiptResponse(
    string ReceiptNumber,
    string CompanyName,
    string? CompanyPhone,
    string? CompanyTaxNumber,
    string TrackingNumber,
    string PayerName,
    string Description,
    decimal Amount,
    string Currency,
    string Method,
    string Provider,
    string? ProviderReference,
    DateTimeOffset PaidAtUtc,
    string? ReceivedBy);

/// <summary>Cash taken at the office for a parcel booked to be paid in cash. The amount must equal the charge exactly.</summary>
public sealed record RecordOfficeCashRequest(Guid ParcelId, decimal AmountReceived, string? Note = null);

/// <summary>A transaction in the accountant's list.</summary>
public sealed record PaymentTransactionRow(
    Guid Id,
    Guid PaymentId,
    Guid ParcelId,
    string TrackingNumber,
    string Reference,
    string Type,
    string Status,
    string Provider,
    string? ProviderReference,
    decimal Amount,
    string Currency,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>Filters for the transaction list.</summary>
public sealed record PaymentTransactionQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    /// <summary>Esewa, Khalti, Cash, CashOnDelivery, BankTransfer or ConnectIps.</summary>
    public string? Provider { get; init; }

    /// <summary>Pending, Succeeded or Failed.</summary>
    public string? Status { get; init; }

    public Guid? ParcelId { get; init; }

    public Guid? BranchId { get; init; }

    /// <summary>Part of the merchant reference, provider reference or tracking number.</summary>
    public string? Search { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }
}

// ---- COD settlement and agent cash ----------------------------------------------------------------------------------

/// <summary>An agent handing in all the cash they have collected and not yet handed in.</summary>
public sealed record CreateSettlementRequest(string? Remarks = null);

/// <summary>The accountant's count of the cash received. If it differs from the total the settlement becomes disputed.</summary>
public sealed record VerifySettlementRequest(decimal CountedAmount, string? Remarks = null);

/// <summary>Accepts a disputed settlement after looking into it. A written explanation is required.</summary>
public sealed record ResolveSettlementRequest(string Remarks);

public sealed record SettlementCollectionDto(
    Guid ParcelId,
    string TrackingNumber,
    decimal ExpectedAmount,
    decimal CollectedAmount,
    DateTimeOffset CollectedAtUtc,
    string Status);

/// <summary>A cash hand-in. Collections are listed in the detail view only.</summary>
public sealed record SettlementResponse(
    Guid Id,
    string ReferenceNumber,
    Guid BranchId,
    string BranchName,
    Guid? AgentId,
    string? AgentName,
    decimal TotalAmount,
    decimal? CountedAmount,
    decimal? Variance,
    int CollectionCount,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? VerifiedAtUtc,
    string? Remarks,
    IReadOnlyList<SettlementCollectionDto> Collections);

/// <summary>Filters for the settlement list.</summary>
public sealed record SettlementQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    /// <summary>Submitted, Verified or Disputed.</summary>
    public string? Status { get; init; }

    public Guid? AgentId { get; init; }

    public Guid? BranchId { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }
}

/// <summary>
/// Where one agent's cash stands. <c>InHand</c> is collected and not handed in; <c>InSettlement</c> is handed in and waiting for
/// the accountant's count; <c>Disputed</c> is a count that did not match; <c>Settled</c> is counted and accepted.
/// </summary>
public sealed record AgentCashPositionRow(
    Guid AgentId,
    string FullName,
    string BranchName,
    decimal Collected,
    decimal InHand,
    decimal InSettlement,
    decimal Disputed,
    decimal Settled,
    int CollectionCount,
    DateTimeOffset? LastCollectionAtUtc);

public sealed record AgentCashQuery
{
    public Guid? BranchId { get; init; }

    public Guid? AgentId { get; init; }
}

/// <summary>The signed-in agent's own cash position and recent hand-ins.</summary>
public sealed record AgentCashSummaryResponse(AgentCashPositionRow Position, IReadOnlyList<SettlementResponse> RecentSettlements);

// ---- accountant reports ---------------------------------------------------------------------------------------------

/// <summary>The period and branch a report covers. Dates are Nepal-time calendar days; the default is the last 30 days.</summary>
public sealed record PaymentReportQuery
{
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public Guid? BranchId { get; init; }
}

public sealed record MethodTotal(string Method, int Count, decimal Amount);

public sealed record ProviderTotal(string Provider, int Succeeded, int Failed, int Pending, decimal Amount);

public sealed record DailyTotal(DateOnly Date, int Count, decimal Amount);

public sealed record CodReport(
    int CollectionCount,
    decimal Collected,
    decimal InHand,
    decimal InSettlement,
    decimal Disputed,
    decimal Settled);

public sealed record SettlementReport(int Submitted, int Verified, int Disputed, decimal TotalVariance);

/// <summary>
/// Delivery charges paid in the period (by the time they were paid), what is still pending (by booking time), the split by
/// method and provider, a daily series, the cash-on-delivery position and the settlements.
/// </summary>
public sealed record PaymentReportResponse(
    DateOnly From,
    DateOnly To,
    Guid? BranchId,
    int PaidCount,
    decimal PaidAmount,
    int PendingCount,
    decimal PendingAmount,
    int FailedAttempts,
    IReadOnlyList<MethodTotal> ByMethod,
    IReadOnlyList<ProviderTotal> ByProvider,
    IReadOnlyList<DailyTotal> Daily,
    CodReport Cod,
    SettlementReport Settlements);
