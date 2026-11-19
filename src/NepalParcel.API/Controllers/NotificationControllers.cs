using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NepalParcel.API.Options;
using NepalParcel.Application.Notifications;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Notifications;
using NepalParcel.Domain.Identity;

namespace NepalParcel.API.Controllers;

/// <summary>
/// WhatsApp and SMS notifications: the messages and what became of them, the message texts, the numbers that opted out, the
/// customer's own choices, and (in test mode only) the mock provider's inbox. Reading is for administrators and branch
/// managers; changing things is for the super administrator.
/// </summary>
[ApiController]
[Route("api/v1/notifications")]
[Produces("application/json")]
public sealed class NotificationsController(INotificationService notifications, INotificationPreferenceService preferences) : ControllerBase
{
    // ---- messages ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The messages, newest first. <c>status</c> is what we did with a message; <c>deliveryStatus</c> is only what the provider
    /// reported afterwards. A message the provider merely accepted has status <c>Submitted</c> and delivery status <c>None</c>.
    /// </summary>
    [HttpGet("messages")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<PagedResponse<NotificationMessageResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResponse<NotificationMessageResponse>>> Search([FromQuery] NotificationMessageQuery query, CancellationToken cancellationToken) =>
        Ok(await notifications.SearchAsync(query, cancellationToken));

    /// <summary>One message with every attempt and every report the provider sent about it.</summary>
    [HttpGet("messages/{id:guid}")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<NotificationMessageDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NotificationMessageDetailResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await notifications.GetAsync(id, cancellationToken));

    /// <summary>Gives a message that failed for good (or is waiting to retry) a fresh set of attempts, starting now.</summary>
    [HttpPost("messages/{id:guid}/requeue")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [ProducesResponseType<NotificationMessageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<NotificationMessageResponse>> Requeue(Guid id, [FromBody] RequeueNotificationRequest? request, CancellationToken cancellationToken) =>
        Ok(await notifications.RequeueAsync(id, request, cancellationToken));

    /// <summary>Stops a message that is waiting or waiting to retry from being sent.</summary>
    [HttpPost("messages/{id:guid}/cancel")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [ProducesResponseType<NotificationMessageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<NotificationMessageResponse>> Cancel(Guid id, [FromBody] RequeueNotificationRequest? request, CancellationToken cancellationToken) =>
        Ok(await notifications.CancelAsync(id, request, cancellationToken));

    /// <summary>Counts of messages by channel and by what happened to them, for a period of Nepal days (default the last 30).</summary>
    [HttpGet("summary")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<NotificationSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NotificationSummaryResponse>> Summary([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken) =>
        Ok(await notifications.SummaryAsync(from, to, cancellationToken));

    /// <summary>Which provider is in use for each channel and whether it is a mock. Never shows credentials.</summary>
    [HttpGet("providers")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<IReadOnlyList<NotificationProviderInfo>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<NotificationProviderInfo>> Providers() => Ok(notifications.Providers());

    /// <summary>A parcel's notifications, oldest first.</summary>
    [HttpGet("~/api/v1/parcels/{parcelId:guid}/notifications")]
    [Authorize(Roles = Roles.ParcelViewersCsv)]
    [ProducesResponseType<IReadOnlyList<NotificationMessageResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NotificationMessageResponse>>> ForParcel(Guid parcelId, CancellationToken cancellationToken) =>
        Ok(await notifications.ListForParcelAsync(parcelId, cancellationToken));

    // ---- templates ---------------------------------------------------------------------------------------------------

    /// <summary>Every message text, with the placeholders each message may use.</summary>
    [HttpGet("templates")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<NotificationTemplatesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<NotificationTemplatesResponse>> Templates(CancellationToken cancellationToken) =>
        Ok(await notifications.ListTemplatesAsync(cancellationToken));

    /// <summary>
    /// Changes a text. For WhatsApp the text is our copy: Meta sends the template it approved under
    /// <c>providerTemplateName</c>, so that template must have the same fields in the same order.
    /// </summary>
    [HttpPut("templates/{id:guid}")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [ProducesResponseType<NotificationTemplateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<NotificationTemplateResponse>> UpdateTemplate(Guid id, [FromBody] UpdateNotificationTemplateRequest request, CancellationToken cancellationToken) =>
        Ok(await notifications.UpdateTemplateAsync(id, request, cancellationToken));

    /// <summary>Fills a text with sample values and lists what is wrong with it, without saving anything.</summary>
    [HttpPost("templates/preview")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<PreviewTemplateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PreviewTemplateResponse>> PreviewTemplate([FromBody] PreviewTemplateRequest request, CancellationToken cancellationToken) =>
        Ok(await notifications.PreviewTemplateAsync(request, cancellationToken));

    // ---- opt-outs ----------------------------------------------------------------------------------------------------

    /// <summary>Numbers that asked not to be messaged on a channel (by replying STOP, or recorded by staff).</summary>
    [HttpGet("opt-outs")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<PagedResponse<OptOutResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<OptOutResponse>>> OptOuts([FromQuery] OptOutQuery query, CancellationToken cancellationToken) =>
        Ok(await notifications.SearchOptOutsAsync(query, cancellationToken));

    /// <summary>Records that a number must not be messaged on a channel. Takes effect for messages not yet sent.</summary>
    [HttpPost("opt-outs")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [ProducesResponseType<OptOutResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OptOutResponse>> CreateOptOut([FromBody] CreateOptOutRequest request, CancellationToken cancellationToken)
    {
        var created = await notifications.CreateOptOutAsync(request, cancellationToken);
        return Created($"/api/v1/notifications/opt-outs/{created.Id}", created);
    }

    /// <summary>Lifts an opt-out. The record stays as history.</summary>
    [HttpDelete("opt-outs/{id:guid}")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RemoveOptOut(Guid id, CancellationToken cancellationToken)
    {
        await notifications.RemoveOptOutAsync(id, cancellationToken);
        return NoContent();
    }

    // ---- consent -----------------------------------------------------------------------------------------------------

    /// <summary>The signed-in customer's own choices about SMS and WhatsApp updates. Both are off until the customer turns them on.</summary>
    [HttpGet("preferences")]
    [Authorize(Roles = Roles.Customer)]
    [ProducesResponseType<NotificationPreferencesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NotificationPreferencesResponse>> GetPreferences(CancellationToken cancellationToken) =>
        Ok(await preferences.GetMineAsync(cancellationToken));

    /// <summary>Changes the customer's choices. Each change is a new consent record; earlier ones are kept.</summary>
    [HttpPut("preferences")]
    [Authorize(Roles = Roles.Customer)]
    [ProducesResponseType<NotificationPreferencesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NotificationPreferencesResponse>> UpdatePreferences([FromBody] UpdateNotificationPreferencesRequest request, CancellationToken cancellationToken) =>
        Ok(await preferences.UpdateMineAsync(request, cancellationToken));

    /// <summary>What the sender of a parcel has agreed to, for counter staff taking a booking.</summary>
    [HttpGet("consent/parcel/{parcelId:guid}")]
    [Authorize(Roles = Roles.CounterStaffCsv)]
    [ProducesResponseType<NotificationPreferencesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NotificationPreferencesResponse>> GetSenderConsent(Guid parcelId, CancellationToken cancellationToken) =>
        Ok(await preferences.GetForParcelSenderAsync(parcelId, cancellationToken));

    /// <summary>Records what the sender of a parcel said at the counter.</summary>
    [HttpPut("consent/parcel/{parcelId:guid}")]
    [Authorize(Roles = Roles.CounterStaffCsv)]
    [ProducesResponseType<NotificationPreferencesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NotificationPreferencesResponse>> UpdateSenderConsent(Guid parcelId, [FromBody] UpdateNotificationPreferencesRequest request, CancellationToken cancellationToken) =>
        Ok(await preferences.UpdateForParcelSenderAsync(parcelId, request, cancellationToken));

    // ---- the mock provider -------------------------------------------------------------------------------------------

    /// <summary>What the mock providers "sent", newest first. Answers 404 when no mock provider is in use.</summary>
    [HttpGet("mock/messages")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [ProducesResponseType<PagedResponse<MockProviderMessageResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<MockProviderMessageResponse>>> MockMessages(
        [FromQuery] string? channel,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await notifications.MockInboxAsync(channel, page, pageSize, cancellationToken));

    /// <summary>
    /// Makes the mock provider report that a message was sent, delivered, read or failed. It goes through the same webhook code a
    /// real provider's report does, so duplicates and out-of-order reports behave as they would in production.
    /// </summary>
    [HttpPost("mock/messages/{providerMessageId}/status")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [ProducesResponseType<WebhookAcceptedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<WebhookAcceptedResponse>> SimulateStatus(string providerMessageId, [FromBody] SimulateStatusRequest request, CancellationToken cancellationToken) =>
        Ok(await notifications.SimulateStatusAsync(providerMessageId, request, cancellationToken));
}

/// <summary>
/// Where providers tell us what happened to a message. The calls are anonymous because the provider has no login: each is
/// proved by a signature over the exact bytes sent (WhatsApp) and nothing is read before the signature checks out.
/// </summary>
[ApiController]
[Route("api/v1/webhooks")]
public sealed class WebhooksController(INotificationWebhookService webhooks) : ControllerBase
{
    private const int MaxBodyBytes = 512 * 1024;
    private const string MetaSignatureHeader = "X-Hub-Signature-256";

    /// <summary>
    /// Meta's check of the endpoint when the webhook is set up: it sends a challenge and the verify token typed into Meta's
    /// console, and expects the challenge back if the token is right.
    /// </summary>
    [HttpGet("whatsapp")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.WebhookPolicy)]
    [Produces("text/plain")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult VerifyWhatsApp(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge) =>
        webhooks.VerifyMetaSubscription(mode, verifyToken, challenge) is { } answer
            ? Content(answer, "text/plain")
            : StatusCode(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Delivery reports and replies from WhatsApp. The signature (<c>X-Hub-Signature-256</c>) is checked against the raw body;
    /// a missing or wrong one is refused with 401. Each report is stored once under its own id, so a repeat is acknowledged and
    /// changes nothing.
    /// </summary>
    [HttpPost("whatsapp")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.WebhookPolicy)]
    [RequestSizeLimit(MaxBodyBytes)]
    [Consumes("application/json")]
    [ProducesResponseType<WebhookAcceptedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<WebhookAcceptedResponse>> ReceiveWhatsApp(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        var signature = Request.Headers[MetaSignatureHeader].ToString();
        return Ok(await webhooks.HandleMetaAsync(buffer.ToArray(), signature, cancellationToken));
    }

    /// <summary>A delivery report from the mock providers. Super administrators only; answers 404 when no mock provider is in use.</summary>
    [HttpPost("mock")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [Consumes("application/json")]
    [ProducesResponseType<WebhookAcceptedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WebhookAcceptedResponse>> ReceiveMock([FromBody] MockWebhookRequest request, CancellationToken cancellationToken) =>
        Ok(await webhooks.HandleMockAsync(request, cancellationToken));
}
