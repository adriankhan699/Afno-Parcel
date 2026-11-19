using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NepalParcel.API.Options;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Tracking;

namespace NepalParcel.API.Controllers;

/// <summary>
/// Parcel tracking for customers. The public view needs only the tracking number and shows no personal data. Detailed
/// tracking needs a one-time code sent to the receiver, or a signed-in sender or staff member.
/// </summary>
[ApiController]
[Route("api/v1/tracking")]
[Produces("application/json")]
public sealed class TrackingController(ITrackingService trackingService) : ControllerBase
{
    public const string AccessHeader = "X-Tracking-Access";

    /// <summary>
    /// Public tracking: the current status, the districts the parcel travels between, the estimated delivery and the timeline.
    /// Never contains names, phone numbers, street addresses, amounts or staff remarks.
    /// </summary>
    [HttpGet("{trackingNumber}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.TrackingPolicy)]
    [ProducesResponseType<PublicTrackingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicTrackingResponse>> GetPublic(string trackingNumber, CancellationToken cancellationToken) =>
        Ok(await trackingService.GetPublicAsync(trackingNumber, cancellationToken));

    /// <summary>
    /// Sends a 6-digit code to the receiver's phone. Always answers 202 with the same message, whether or not the tracking
    /// number exists, so it cannot be used to find valid numbers or to learn who a receiver is.
    /// </summary>
    [HttpPost("{trackingNumber}/otp")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.TrackingOtpPolicy)]
    [ProducesResponseType<TrackingOtpRequestedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<TrackingOtpRequestedResponse>> RequestOtp(string trackingNumber, CancellationToken cancellationToken) =>
        Accepted(await trackingService.RequestOtpAsync(trackingNumber, cancellationToken));

    /// <summary>
    /// Checks the code. On success returns a short-lived token for this tracking number only. A wrong, expired or locked code
    /// always gets the same 400 answer.
    /// </summary>
    [HttpPost("{trackingNumber}/verify")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.TrackingOtpPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<TrackingAccessResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<TrackingAccessResponse>> Verify(
        string trackingNumber,
        [FromBody] VerifyTrackingOtpRequest request,
        CancellationToken cancellationToken) =>
        Ok(await trackingService.VerifyOtpAsync(trackingNumber, request, cancellationToken));

    /// <summary>
    /// Detailed tracking. Send the <c>X-Tracking-Access</c> token from <c>/verify</c> (receiver), or a normal bearer token
    /// (the sender, or staff). Without either the answer is 401.
    /// </summary>
    [HttpGet("{trackingNumber}/details")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.TrackingPolicy)]
    [ProducesResponseType<DetailedTrackingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DetailedTrackingResponse>> GetDetails(
        string trackingNumber,
        [FromHeader(Name = AccessHeader)] string? accessToken,
        CancellationToken cancellationToken) =>
        Ok(await trackingService.GetDetailsAsync(trackingNumber, accessToken, cancellationToken));
}
