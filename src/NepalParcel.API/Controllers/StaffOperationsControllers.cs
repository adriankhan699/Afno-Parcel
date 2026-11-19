using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NepalParcel.API.Options;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Contracts.Warehouses;
using NepalParcel.Domain.Identity;

namespace NepalParcel.API.Controllers;

/// <summary>Staff updates to a parcel's status. Every update adds a row to the parcel's append-only history.</summary>
[ApiController]
[Route("api/v1/parcels")]
[Produces("application/json")]
public sealed class ParcelStatusController(IParcelStatusService statusService) : ControllerBase
{
    /// <summary>
    /// Moves a parcel to its next status. The move must follow the workflow, the caller's role must be allowed to make it,
    /// and remarks or a branch/warehouse are required where the status needs them.
    /// </summary>
    [HttpPost("{id:guid}/status")]
    [Authorize(Roles = Roles.StatusUpdatersCsv)]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<ParcelStatusUpdateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ParcelStatusUpdateResponse>> UpdateStatus(
        Guid id,
        [FromBody] UpdateParcelStatusRequest request,
        CancellationToken cancellationToken) =>
        Ok(await statusService.UpdateAsync(id, request, cancellationToken));

    /// <summary>The same as updating by id, for staff who only have the tracking number (for example from a label).</summary>
    [HttpPost("tracking/{trackingNumber}/status")]
    [Authorize(Roles = Roles.StatusUpdatersCsv)]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<ParcelStatusUpdateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ParcelStatusUpdateResponse>> UpdateStatusByTrackingNumber(
        string trackingNumber,
        [FromBody] UpdateParcelStatusRequest request,
        CancellationToken cancellationToken) =>
        Ok(await statusService.UpdateByTrackingNumberAsync(trackingNumber, request, cancellationToken));
}

/// <summary>Warehouse scanning: the foundation for barcode scanners at hubs.</summary>
[ApiController]
[Route("api/v1/scans")]
[Produces("application/json")]
[Authorize(Roles = Roles.ScannersCsv)]
public sealed class ScansController(IParcelStatusService statusService) : ControllerBase
{
    /// <summary>Records one scan. Arrival → arrived at warehouse, Dispatch → in transit, OutForDelivery → out for delivery.</summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<ParcelStatusUpdateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ParcelStatusUpdateResponse>> Scan([FromBody] ScanRequest request, CancellationToken cancellationToken) =>
        Ok(await statusService.ScanAsync(request, cancellationToken));

    /// <summary>
    /// Records many scans of the same type at one warehouse (up to 100). Each parcel succeeds or fails on its own; one bad
    /// tracking number never stops the others.
    /// </summary>
    [HttpPost("batch")]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<BatchScanResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BatchScanResponse>> ScanBatch([FromBody] BatchScanRequest request, CancellationToken cancellationToken) =>
        Ok(await statusService.ScanBatchAsync(request, cancellationToken));
}

/// <summary>Warehouses and sorting hubs.</summary>
[ApiController]
[Route("api/v1/warehouses")]
[Produces("application/json")]
public sealed class WarehousesController(IWarehouseService warehouseService) : ControllerBase
{
    /// <summary>Lists warehouses (staff who work with parcel status).</summary>
    [HttpGet]
    [Authorize(Roles = Roles.StatusUpdatersCsv)]
    [ProducesResponseType<PagedResponse<WarehouseResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<WarehouseResponse>>> GetWarehouses([FromQuery] PageQuery query, CancellationToken cancellationToken) =>
        Ok(await warehouseService.GetPageAsync(query, cancellationToken));

    /// <summary>Registers a warehouse. SuperAdmin and BranchManager only.</summary>
    [HttpPost]
    [Authorize(Roles = $"{Roles.SuperAdmin},{Roles.BranchManager}")]
    [Consumes("application/json")]
    [ProducesResponseType<WarehouseResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WarehouseResponse>> CreateWarehouse([FromBody] CreateWarehouseRequest request, CancellationToken cancellationToken)
    {
        var created = await warehouseService.CreateAsync(request, cancellationToken);
        return Created(new Uri($"/api/v1/warehouses/{created.Id}", UriKind.Relative), created);
    }
}
