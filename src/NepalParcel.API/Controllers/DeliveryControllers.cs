using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NepalParcel.API.Options;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Delivery;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Delivery;
using NepalParcel.Domain.Identity;

namespace NepalParcel.API.Controllers;

/// <summary>Creating and managing delivery agents, and seeing how they do. SuperAdmin, and branch managers for their own branch.</summary>
[ApiController]
[Route("api/v1/delivery-agents")]
[Authorize(Roles = Roles.NetworkAdminsCsv)]
[Produces("application/json")]
public sealed class DeliveryAgentsController(IDeliveryAgentService agentService) : ControllerBase
{
    /// <summary>Lists agents, A to Z, with how many open assignments each has.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResponse<DeliveryAgentResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<DeliveryAgentResponse>>> GetAgents([FromQuery] DeliveryAgentQuery query, CancellationToken cancellationToken) =>
        Ok(await agentService.GetPageAsync(query, cancellationToken));

    /// <summary>Gets one agent.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<DeliveryAgentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeliveryAgentResponse>> GetAgent(Guid id, CancellationToken cancellationToken) =>
        Ok(await agentService.GetByIdAsync(id, cancellationToken));

    /// <summary>Creates an agent: the login, the agent profile, and the assignment to the branch, in one step.</summary>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<DeliveryAgentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeliveryAgentResponse>> CreateAgent([FromBody] CreateDeliveryAgentRequest request, CancellationToken cancellationToken)
    {
        var created = await agentService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetAgent), new { id = created.Id }, created);
    }

    /// <summary>Changes an agent's phone number and vehicle details.</summary>
    [HttpPut("{id:guid}")]
    [Consumes("application/json")]
    [ProducesResponseType<DeliveryAgentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeliveryAgentResponse>> UpdateAgent(Guid id, [FromBody] UpdateDeliveryAgentRequest request, CancellationToken cancellationToken) =>
        Ok(await agentService.UpdateAsync(id, request, cancellationToken));

    /// <summary>Lets a switched-off agent sign in and work again.</summary>
    [HttpPost("{id:guid}/activate")]
    [ProducesResponseType<DeliveryAgentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeliveryAgentResponse>> Activate(Guid id, CancellationToken cancellationToken) =>
        Ok(await agentService.ActivateAsync(id, cancellationToken));

    /// <summary>Switches an agent off: they can no longer sign in. Refused (422) while they still have open assignments.</summary>
    [HttpPost("{id:guid}/deactivate")]
    [ProducesResponseType<DeliveryAgentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DeliveryAgentResponse>> Deactivate(Guid id, CancellationToken cancellationToken) =>
        Ok(await agentService.DeactivateAsync(id, cancellationToken));

    /// <summary>Marks an agent as available (or not) for new assignments.</summary>
    [HttpPut("{id:guid}/availability")]
    [Consumes("application/json")]
    [ProducesResponseType<DeliveryAgentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DeliveryAgentResponse>> SetAvailability(Guid id, [FromBody] SetAvailabilityRequest request, CancellationToken cancellationToken) =>
        Ok(await agentService.SetAvailabilityAsync(id, request, cancellationToken));

    /// <summary>
    /// How each agent did over a period (default: the last 30 Nepal days): parcels assigned, delivered, failed and taken back,
    /// success rate, first-attempt success rate, average time from assignment to delivery, and cash collected.
    /// </summary>
    [HttpGet("performance")]
    [ProducesResponseType<AgentPerformanceReport>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AgentPerformanceReport>> GetPerformance([FromQuery] AgentPerformanceQuery query, CancellationToken cancellationToken) =>
        Ok(await agentService.GetPerformanceAsync(query, cancellationToken));
}

/// <summary>Giving parcels to delivery agents, taking them back, and looking at the proofs.</summary>
[ApiController]
[Route("api/v1/delivery")]
[Produces("application/json")]
public sealed class DeliveryController(IDeliveryAssignmentService assignmentService) : ControllerBase
{
    /// <summary>Parcels that are ready for a delivery agent and have none yet, longest-waiting first.</summary>
    [HttpGet("ready")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<PagedResponse<ReadyParcelResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<ReadyParcelResponse>>> GetReady([FromQuery] ReadyParcelQuery query, CancellationToken cancellationToken) =>
        Ok(await assignmentService.GetReadyAsync(query, cancellationToken));

    /// <summary>
    /// Gives a parcel to an agent for the final leg. The agent must be active and available, the parcel must be ready for
    /// delivery and not already assigned, and a branch manager must work with their own branch's agents and parcels.
    /// </summary>
    [HttpPost("assignments")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<DeliveryAssignmentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DeliveryAssignmentResponse>> Assign([FromBody] AssignDeliveryRequest request, CancellationToken cancellationToken)
    {
        var created = await assignmentService.AssignAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetAssignments), new { parcelId = created.ParcelId }, created);
    }

    /// <summary>Lists delivery assignments, newest first. A branch manager sees only their own branch's agents.</summary>
    [HttpGet("assignments")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<PagedResponse<DeliveryAssignmentResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<DeliveryAssignmentResponse>>> GetAssignments([FromQuery] DeliveryAssignmentQuery query, CancellationToken cancellationToken) =>
        Ok(await assignmentService.GetPageAsync(query, cancellationToken));

    /// <summary>Takes a parcel back from an agent who has not taken it out yet. A reason is required.</summary>
    [HttpPost("assignments/{id:guid}/cancel")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<DeliveryAssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DeliveryAssignmentResponse>> Cancel(Guid id, [FromBody] CancelAssignmentRequest request, CancellationToken cancellationToken) =>
        Ok(await assignmentService.CancelAsync(id, request, cancellationToken));

    /// <summary>
    /// The signature or photo of a proof of delivery, for staff who work with parcels. The file lives in private storage and
    /// is only ever served here, to a signed-in person.
    /// </summary>
    [HttpGet("proofs/{proofId:guid}/file")]
    [Authorize(Roles = Roles.ParcelViewersCsv)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProofFile(Guid proofId, CancellationToken cancellationToken)
    {
        var file = await assignmentService.GetProofFileAsync(proofId, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(file.Content, file.ContentType);
    }
}

/// <summary>The form a signature or photo is sent in.</summary>
public sealed class ProofFileForm
{
    /// <summary>Signature or Photo.</summary>
    public string? Type { get; init; }

    /// <summary>Who signed or received (required for a signature).</summary>
    public string? RecipientName { get; init; }

    /// <summary>The picture: PNG, JPEG or WebP.</summary>
    public IFormFile? File { get; init; }
}

/// <summary>
/// The delivery agent's own work. Only signed-in delivery agents with an active profile can use it, and each agent can only
/// ever see and change the assignments given to them.
/// </summary>
[ApiController]
[Route("api/v1/agent")]
[Authorize(Roles = Roles.DeliveryAgent)]
[Produces("application/json")]
public sealed class AgentController(IAgentWorkService workService, IOptions<DeliveryOptions> deliveryOptions) : ControllerBase
{
    /// <summary>My profile and how my day is going.</summary>
    [HttpGet("me")]
    [ProducesResponseType<AgentSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AgentSummaryResponse>> GetMe(CancellationToken cancellationToken) =>
        Ok(await workService.GetSummaryAsync(cancellationToken));

    /// <summary>Marks myself as available (or not) for new assignments.</summary>
    [HttpPut("me/availability")]
    [Consumes("application/json")]
    [ProducesResponseType<AgentSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AgentSummaryResponse>> SetAvailability([FromBody] SetAvailabilityRequest request, CancellationToken cancellationToken) =>
        Ok(await workService.SetAvailabilityAsync(request, cancellationToken));

    /// <summary>My assignments: "open" (to do, or failed and waiting to go back to a warehouse) or "history".</summary>
    [HttpGet("assignments")]
    [ProducesResponseType<PagedResponse<AgentAssignmentResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<AgentAssignmentResponse>>> GetAssignments([FromQuery] AgentAssignmentQuery query, CancellationToken cancellationToken) =>
        Ok(await workService.GetAssignmentsAsync(query, cancellationToken));

    /// <summary>One of my assignments. Anyone else's looks like it does not exist (404).</summary>
    [HttpGet("assignments/{id:guid}")]
    [ProducesResponseType<AgentAssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentAssignmentResponse>> GetAssignment(Guid id, CancellationToken cancellationToken) =>
        Ok(await workService.GetAssignmentAsync(id, cancellationToken));

    /// <summary>Accepts an assignment (optional).</summary>
    [HttpPost("assignments/{id:guid}/accept")]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [ProducesResponseType<AgentAssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AgentAssignmentResponse>> Accept(Guid id, CancellationToken cancellationToken) =>
        Ok(await workService.AcceptAsync(id, cancellationToken));

    /// <summary>Takes the parcel out for delivery: the parcel becomes OutForDelivery.</summary>
    [HttpPost("assignments/{id:guid}/start")]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [ProducesResponseType<AgentAssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AgentAssignmentResponse>> Start(Guid id, CancellationToken cancellationToken) =>
        Ok(await workService.StartAsync(id, cancellationToken));

    /// <summary>Marks the delivery attempt as failed. A reason is required.</summary>
    [HttpPost("assignments/{id:guid}/fail")]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<AgentAssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AgentAssignmentResponse>> Fail(Guid id, [FromBody] MarkDeliveryFailedRequest request, CancellationToken cancellationToken) =>
        Ok(await workService.FailAsync(id, request, cancellationToken));

    /// <summary>Sends a one-time code to the receiver's phone. The agent never sees it; the receiver reads it out.</summary>
    [HttpPost("assignments/{id:guid}/delivery-code")]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [ProducesResponseType<DeliveryCodeSentResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DeliveryCodeSentResponse>> SendDeliveryCode(Guid id, CancellationToken cancellationToken) =>
        Accepted(await workService.SendDeliveryCodeAsync(id, cancellationToken));

    /// <summary>Records the receiver's code as proof of delivery.</summary>
    [HttpPost("assignments/{id:guid}/proof/otp")]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<AgentAssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AgentAssignmentResponse>> VerifyCode(Guid id, [FromBody] VerifyDeliveryCodeRequest request, CancellationToken cancellationToken) =>
        Ok(await workService.VerifyDeliveryCodeAsync(id, request, cancellationToken));

    /// <summary>Records a signature or a photo as proof of delivery (multipart form: type, recipientName, file).</summary>
    [HttpPost("assignments/{id:guid}/proof/file")]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType<AgentAssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AgentAssignmentResponse>> RecordFileProof(Guid id, [FromForm] ProofFileForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.File is null || form.File.Length == 0)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["File"] = ["Choose a file."] });
        }

        // Read no more than one byte past the limit: the service then refuses an oversized file without it all being loaded.
        var limit = (deliveryOptions.Value.MaxProofSizeKb * 1024L) + 1;
        var length = (int)Math.Min(form.File.Length, limit);
        var content = new byte[length];
        await using (var stream = form.File.OpenReadStream())
        {
            await stream.ReadExactlyAsync(content, cancellationToken);
        }

        return Ok(await workService.RecordFileProofAsync(id, form.Type ?? string.Empty, form.RecipientName, content, cancellationToken));
    }

    /// <summary>Records the cash taken from the receiver. It must equal the amount due exactly.</summary>
    [HttpPost("assignments/{id:guid}/collect-cod")]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<CodCollectionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CodCollectionResponse>> CollectCod(Guid id, [FromBody] CollectCodRequest request, CancellationToken cancellationToken) =>
        Ok(await workService.CollectCodAsync(id, request, cancellationToken));

    /// <summary>Confirms the delivery. Needs a proof, and the cash if the parcel is cash on delivery.</summary>
    [HttpPost("assignments/{id:guid}/complete")]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [ProducesResponseType<AgentAssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AgentAssignmentResponse>> Complete(Guid id, [FromBody] CompleteDeliveryRequest? request, CancellationToken cancellationToken) =>
        Ok(await workService.CompleteAsync(id, request ?? new CompleteDeliveryRequest(), cancellationToken));

    /// <summary>Takes a parcel whose delivery failed back to a warehouse.</summary>
    [HttpPost("assignments/{id:guid}/return-to-warehouse")]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<AgentAssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AgentAssignmentResponse>> ReturnToWarehouse(Guid id, [FromBody] ReturnToWarehouseRequest request, CancellationToken cancellationToken) =>
        Ok(await workService.ReturnToWarehouseAsync(id, request, cancellationToken));

    private const int MaxRequestBytes = 12 * 1024 * 1024;
}
