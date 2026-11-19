using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NepalParcel.API.Options;
using NepalParcel.Application.Payments;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Payments;
using NepalParcel.Domain.Identity;

namespace NepalParcel.API.Controllers;

/// <summary>
/// Paying for parcels: online payments (eSewa and Khalti, simulated in test mode), cash at the office, receipts and the
/// accountant's lists and reports. A payment is only ever marked as paid after the server has asked the provider itself.
/// </summary>
[ApiController]
[Route("api/v1/payments")]
[Produces("application/json")]
public sealed class PaymentsController(
    IPaymentService paymentService,
    ITestGatewayService testGateway,
    ISettlementService settlementService,
    IPaymentReportService reportService) : ControllerBase
{
    // ---- paying online ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts an online payment of a parcel's delivery charge and returns where to send the customer. The provider must be the
    /// one the parcel was booked with. Starting again reuses the open attempt, so it is safe to repeat.
    /// </summary>
    [HttpPost("initiate")]
    [Authorize(Roles = Roles.ParcelBookersCsv)]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<InitiatePaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<InitiatePaymentResponse>> Initiate([FromBody] InitiatePaymentRequest request, CancellationToken cancellationToken) =>
        Ok(await paymentService.InitiateAsync(request, cancellationToken));

    /// <summary>
    /// The customer's browser came back from the provider. Nothing in this request is believed: the server asks the provider
    /// what happened and checks the reference, amount and currency. Repeating the call for a finished attempt changes nothing.
    /// </summary>
    [HttpPost("callback/{provider}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.TrackingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<PaymentStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PaymentStatusResponse>> Callback(string provider, [FromBody] PaymentCallbackRequest request, CancellationToken cancellationToken) =>
        Ok(await paymentService.VerifyAsync(provider, request, cancellationToken));

    /// <summary>Where an attempt stands, by its (unguessable) reference. Shows nothing personal.</summary>
    [HttpGet("status/{reference}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.TrackingPolicy)]
    [ProducesResponseType<PaymentStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentStatusResponse>> GetStatus(string reference, CancellationToken cancellationToken) =>
        Ok(await paymentService.GetStatusAsync(reference, cancellationToken));

    /// <summary>The simulated provider's page data. Exists only in test mode (404 otherwise).</summary>
    [HttpGet("test-gateway/{reference}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.TrackingPolicy)]
    [ProducesResponseType<TestGatewaySessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TestGatewaySessionResponse>> GetTestSession(string reference, CancellationToken cancellationToken) =>
        Ok(await testGateway.GetSessionAsync(reference, cancellationToken));

    /// <summary>What the tester does on the simulated provider page: pay, fail or cancel. Exists only in test mode.</summary>
    [HttpPost("test-gateway/{reference}/complete")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.TrackingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<TestGatewaySessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TestGatewaySessionResponse>> CompleteTestSession(string reference, [FromBody] TestGatewayCompleteRequest request, CancellationToken cancellationToken) =>
        Ok(await testGateway.CompleteAsync(reference, request, cancellationToken));

    // ---- history, receipts, cash at the office ---------------------------------------------------------------------------

    /// <summary>A parcel's payments with every transaction (attempts, failures, provider references). Customers see only their own.</summary>
    [HttpGet("parcel/{parcelId:guid}")]
    [Authorize(Roles = Roles.ParcelReadersCsv)]
    [ProducesResponseType<IReadOnlyList<PaymentResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<PaymentResponse>>> GetForParcel(Guid parcelId, CancellationToken cancellationToken) =>
        Ok(await paymentService.GetForParcelAsync(parcelId, cancellationToken));

    /// <summary>The printable receipt of a paid payment (422 until it has been paid).</summary>
    [HttpGet("{paymentId:guid}/receipt")]
    [Authorize(Roles = Roles.ParcelReadersCsv)]
    [ProducesResponseType<PaymentReceiptResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PaymentReceiptResponse>> GetReceipt(Guid paymentId, CancellationToken cancellationToken) =>
        Ok(await paymentService.GetReceiptAsync(paymentId, cancellationToken));

    /// <summary>Records cash taken at the office for a parcel booked to be paid in cash. The amount must equal the charge exactly.</summary>
    [HttpPost("office-cash")]
    [Authorize(Roles = Roles.CounterStaffCsv)]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PaymentResponse>> RecordOfficeCash([FromBody] RecordOfficeCashRequest request, CancellationToken cancellationToken) =>
        Ok(await paymentService.RecordOfficeCashAsync(request, cancellationToken));

    // ---- accountant ------------------------------------------------------------------------------------------------------------

    /// <summary>Every payment transaction, newest first. A branch manager sees only their own branch's parcels.</summary>
    [HttpGet("transactions")]
    [Authorize(Roles = Roles.FinanceCsv)]
    [ProducesResponseType<PagedResponse<PaymentTransactionRow>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<PaymentTransactionRow>>> GetTransactions([FromQuery] PaymentTransactionQuery query, CancellationToken cancellationToken) =>
        Ok(await paymentService.SearchTransactionsAsync(query, cancellationToken));

    /// <summary>The accountant's summary for a period (default: the last 30 Nepal days).</summary>
    [HttpGet("reports/summary")]
    [Authorize(Roles = Roles.FinanceCsv)]
    [ProducesResponseType<PaymentReportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PaymentReportResponse>> GetSummary([FromQuery] PaymentReportQuery query, CancellationToken cancellationToken) =>
        Ok(await reportService.GetSummaryAsync(query, cancellationToken));

    /// <summary>Where each agent's cash stands: in hand, waiting for a count, disputed, settled.</summary>
    [HttpGet("agent-cash")]
    [Authorize(Roles = Roles.FinanceCsv)]
    [ProducesResponseType<IReadOnlyList<AgentCashPositionRow>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<AgentCashPositionRow>>> GetAgentCash([FromQuery] AgentCashQuery query, CancellationToken cancellationToken) =>
        Ok(await settlementService.GetAgentCashAsync(query, cancellationToken));

    /// <summary>Cash hand-ins, newest first.</summary>
    [HttpGet("settlements")]
    [Authorize(Roles = Roles.FinanceCsv)]
    [ProducesResponseType<PagedResponse<SettlementResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<SettlementResponse>>> GetSettlements([FromQuery] SettlementQuery query, CancellationToken cancellationToken) =>
        Ok(await settlementService.SearchAsync(query, cancellationToken));

    /// <summary>One cash hand-in with the collections in it.</summary>
    [HttpGet("settlements/{id:guid}")]
    [Authorize(Roles = Roles.FinanceCsv)]
    [ProducesResponseType<SettlementResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SettlementResponse>> GetSettlement(Guid id, CancellationToken cancellationToken) =>
        Ok(await settlementService.GetAsync(id, cancellationToken));

    /// <summary>
    /// Records the count of the cash received. If it equals the total the settlement is accepted and every collection in it is
    /// settled; if it differs the settlement becomes disputed and remarks explaining the difference are required.
    /// </summary>
    [HttpPost("settlements/{id:guid}/verify")]
    [Authorize(Roles = Roles.FinanceCsv)]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<SettlementResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SettlementResponse>> VerifySettlement(Guid id, [FromBody] VerifySettlementRequest request, CancellationToken cancellationToken) =>
        Ok(await settlementService.VerifyAsync(id, request, cancellationToken));

    /// <summary>Accepts a disputed settlement after looking into it. A written explanation is required.</summary>
    [HttpPost("settlements/{id:guid}/resolve")]
    [Authorize(Roles = Roles.FinanceCsv)]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<SettlementResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SettlementResponse>> ResolveSettlement(Guid id, [FromBody] ResolveSettlementRequest request, CancellationToken cancellationToken) =>
        Ok(await settlementService.ResolveAsync(id, request, cancellationToken));
}

/// <summary>The delivery agent's own cash: what they hold and handing it in.</summary>
[ApiController]
[Route("api/v1/agent")]
[Authorize(Roles = Roles.DeliveryAgent)]
[Produces("application/json")]
public sealed class AgentCashController(ISettlementService settlementService) : ControllerBase
{
    /// <summary>My cash position (in hand, waiting for a count, disputed, settled) and my recent hand-ins.</summary>
    [HttpGet("cash")]
    [ProducesResponseType<AgentCashSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AgentCashSummaryResponse>> GetCash(CancellationToken cancellationToken) =>
        Ok(await settlementService.GetMyCashAsync(cancellationToken));

    /// <summary>My cash hand-ins, newest first.</summary>
    [HttpGet("settlements")]
    [ProducesResponseType<PagedResponse<SettlementResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<SettlementResponse>>> GetSettlements(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = PageQuery.DefaultPageSize,
        CancellationToken cancellationToken = default) =>
        Ok(await settlementService.GetMineAsync(page, pageSize, cancellationToken));

    /// <summary>Hands in all the cash I have collected and not yet handed in (422 when there is none).</summary>
    [HttpPost("settlements")]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [ProducesResponseType<SettlementResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SettlementResponse>> HandIn([FromBody] CreateSettlementRequest? request, CancellationToken cancellationToken)
    {
        var created = await settlementService.SubmitAsync(request ?? new CreateSettlementRequest(), cancellationToken);
        return CreatedAtAction(nameof(GetSettlements), new { }, created);
    }
}
