using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NepalParcel.API.Options;
using NepalParcel.Application.Parcels;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Identity;

namespace NepalParcel.API.Controllers;

/// <summary>Booking, searching and cancelling parcels. Customers see only their own parcels; staff see all.</summary>
[ApiController]
[Route("api/v1/parcels")]
[Produces("application/json")]
public sealed class ParcelsController(IParcelService parcelService) : ControllerBase
{
    /// <summary>
    /// Books a parcel. Customers book for themselves online; counter staff book for walk-in customers.
    /// The delivery charge is calculated by the server; the request cannot set a price.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = Roles.ParcelBookersCsv)]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<ParcelResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ParcelResponse>> Book([FromBody] BookParcelRequest request, CancellationToken cancellationToken)
    {
        var parcel = await parcelService.BookAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = parcel.Id }, parcel);
    }

    /// <summary>Searches parcels, newest first. Filter by status, service, payment, channel, COD, branch and booking dates.</summary>
    [HttpGet]
    [Authorize(Roles = Roles.ParcelReadersCsv)]
    [ProducesResponseType<PagedResponse<ParcelSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<ParcelSummaryResponse>>> Search(
        [FromQuery] ParcelSearchQuery query,
        CancellationToken cancellationToken) =>
        Ok(await parcelService.SearchAsync(query, cancellationToken));

    /// <summary>Gets a parcel by id.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Roles = Roles.ParcelReadersCsv)]
    [ProducesResponseType<ParcelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ParcelResponse>> GetById(Guid id, CancellationToken cancellationToken) =>
        Ok(await parcelService.GetByIdAsync(id, cancellationToken));

    /// <summary>Gets a parcel by tracking number (spaces, dashes and lower case are accepted).</summary>
    [HttpGet("tracking/{trackingNumber}")]
    [Authorize(Roles = Roles.ParcelReadersCsv)]
    [ProducesResponseType<ParcelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ParcelResponse>> GetByTrackingNumber(string trackingNumber, CancellationToken cancellationToken) =>
        Ok(await parcelService.GetByTrackingNumberAsync(trackingNumber, cancellationToken));

    /// <summary>Gets the printable booking receipt.</summary>
    [HttpGet("{id:guid}/receipt")]
    [Authorize(Roles = Roles.ParcelReadersCsv)]
    [ProducesResponseType<ReceiptResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceiptResponse>> GetReceipt(Guid id, CancellationToken cancellationToken) =>
        Ok(await parcelService.GetReceiptAsync(id, cancellationToken));

    /// <summary>
    /// Cancels a booking. Customers can cancel only before the parcel is picked up or received at a branch;
    /// counter staff can also cancel after pickup or branch receipt. A reason is required.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = Roles.ParcelBookersCsv)]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<ParcelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ParcelResponse>> Cancel(Guid id, [FromBody] CancelParcelRequest request, CancellationToken cancellationToken) =>
        Ok(await parcelService.CancelAsync(id, request, cancellationToken));

    /// <summary>Records that a pending prepaid delivery charge has been paid (counter staff only).</summary>
    [HttpPost("{id:guid}/confirm-payment")]
    [Authorize(Roles = Roles.CounterStaffCsv)]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<ParcelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ParcelResponse>> ConfirmPayment(
        Guid id,
        [FromBody] ConfirmPaymentRequest request,
        CancellationToken cancellationToken) =>
        Ok(await parcelService.ConfirmPaymentAsync(id, request, cancellationToken));
}
