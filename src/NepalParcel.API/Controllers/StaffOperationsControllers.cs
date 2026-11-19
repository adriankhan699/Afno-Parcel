using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NepalParcel.API.Options;
using NepalParcel.Application.Network;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Staff;
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
    /// and remarks or a branch/warehouse are required where the status needs them. Changes made at a warehouse also record a
    /// warehouse movement, and staff must be assigned to that warehouse's branch.
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

/// <summary>Warehouse scanning: barcodes and QR codes in, arrivals and departures out.</summary>
[ApiController]
[Route("api/v1/scans")]
[Produces("application/json")]
public sealed class ScansController(IScanService scanService) : ControllerBase
{
    /// <summary>
    /// Records one scan. The code can be the tracking number (barcode) or the tracking link (QR code). Arrival → arrived at
    /// warehouse, Dispatch → in transit (optionally to another warehouse), OutForDelivery → handed to a rider. Scanning the
    /// same thing again is answered with Outcome = Duplicate and changes nothing. You must be assigned to the warehouse's branch.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = Roles.ScannersCsv)]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<ScanResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ScanResultDto>> Scan([FromBody] ScanRequest request, CancellationToken cancellationToken) =>
        Ok(await scanService.ScanAsync(request, cancellationToken));

    /// <summary>
    /// Records many scans of the same type at one warehouse (up to 100). Each parcel is Applied, Duplicate or Failed on its own;
    /// one bad code never stops the others.
    /// </summary>
    [HttpPost("batch")]
    [Authorize(Roles = Roles.ScannersCsv)]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<BatchScanResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BatchScanResponse>> ScanBatch([FromBody] BatchScanRequest request, CancellationToken cancellationToken) =>
        Ok(await scanService.ScanBatchAsync(request, cancellationToken));

    /// <summary>The scan log, newest first. Every scan is in it, including repeats.</summary>
    [HttpGet]
    [Authorize(Roles = Roles.ParcelViewersCsv)]
    [ProducesResponseType<PagedResponse<ParcelScanResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<ParcelScanResponse>>> GetScans([FromQuery] ScanLogQuery query, CancellationToken cancellationToken) =>
        Ok(await scanService.SearchAsync(query, cancellationToken));
}

/// <summary>Warehouses and sorting hubs: their details, their inventory and what is on its way to them.</summary>
[ApiController]
[Route("api/v1/warehouses")]
[Produces("application/json")]
public sealed class WarehousesController(IWarehouseService warehouseService, IInventoryService inventoryService) : ControllerBase
{
    /// <summary>Lists warehouses with how many parcels each holds (staff who work with parcels).</summary>
    [HttpGet]
    [Authorize(Roles = Roles.StaffRolesCsv)]
    [ProducesResponseType<PagedResponse<WarehouseResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<WarehouseResponse>>> GetWarehouses([FromQuery] WarehouseQuery query, CancellationToken cancellationToken) =>
        Ok(await warehouseService.GetPageAsync(query, cancellationToken));

    /// <summary>Gets a warehouse by id.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Roles = Roles.StaffRolesCsv)]
    [ProducesResponseType<WarehouseResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WarehouseResponse>> GetWarehouse(Guid id, CancellationToken cancellationToken) =>
        Ok(await warehouseService.GetByIdAsync(id, cancellationToken));

    /// <summary>Registers a warehouse. SuperAdmin, or the BranchManager of the operating branch.</summary>
    [HttpPost]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
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
        return CreatedAtAction(nameof(GetWarehouse), new { id = created.Id }, created);
    }

    /// <summary>Changes a warehouse's name, address, phone number and capacity.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [Consumes("application/json")]
    [ProducesResponseType<WarehouseResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WarehouseResponse>> UpdateWarehouse(Guid id, [FromBody] UpdateWarehouseRequest request, CancellationToken cancellationToken) =>
        Ok(await warehouseService.UpdateAsync(id, request, cancellationToken));

    /// <summary>Switches a warehouse back on (its branch must be active).</summary>
    [HttpPost("{id:guid}/activate")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<WarehouseResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WarehouseResponse>> Activate(Guid id, CancellationToken cancellationToken) =>
        Ok(await warehouseService.ActivateAsync(id, cancellationToken));

    /// <summary>Switches a warehouse off. Refused (422) while it holds parcels or parcels are on their way to it.</summary>
    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<WarehouseResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<WarehouseResponse>> Deactivate(Guid id, CancellationToken cancellationToken) =>
        Ok(await warehouseService.DeactivateAsync(id, cancellationToken));

    /// <summary>What the warehouse holds now (longest-waiting first), what is on its way to it, and how full it is.</summary>
    [HttpGet("{id:guid}/inventory")]
    [Authorize(Roles = Roles.ParcelViewersCsv)]
    [ProducesResponseType<WarehouseInventoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WarehouseInventoryResponse>> GetInventory(
        Guid id,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = PageQuery.DefaultPageSize,
        CancellationToken cancellationToken = default) =>
        Ok(await inventoryService.GetWarehouseInventoryAsync(id, search, page, pageSize, cancellationToken));
}

/// <summary>Parcels that are waiting for somebody to pick them up or send them on.</summary>
[ApiController]
[Route("api/v1/inventory")]
[Produces("application/json")]
public sealed class InventoryController(IInventoryService inventoryService) : ControllerBase
{
    /// <summary>
    /// Parcels waiting for pickup (booked, waiting to be collected) or for dispatch (picked up at a branch, or at a warehouse and
    /// not yet sent on). Longest-waiting first.
    /// </summary>
    [HttpGet("waiting")]
    [Authorize(Roles = Roles.ParcelViewersCsv)]
    [ProducesResponseType<PagedResponse<WaitingParcelDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<WaitingParcelDto>>> GetWaiting([FromQuery] WaitingQuery query, CancellationToken cancellationToken) =>
        Ok(await inventoryService.GetWaitingAsync(query, cancellationToken));
}

/// <summary>Which staff work at which branch or warehouse.</summary>
[ApiController]
[Route("api/v1/staff")]
[Produces("application/json")]
public sealed class StaffController(IStaffService staffService) : ControllerBase
{
    /// <summary>Lists staff accounts and where each works (SuperAdmin and BranchManager).</summary>
    [HttpGet]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<PagedResponse<StaffMemberResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<StaffMemberResponse>>> GetStaff([FromQuery] StaffQuery query, CancellationToken cancellationToken) =>
        Ok(await staffService.ListAsync(query, cancellationToken));

    /// <summary>
    /// Assigns a staff member to a branch, and optionally one of its warehouses. A super administrator can assign anyone anywhere;
    /// a branch manager can only assign staff into their own branch.
    /// </summary>
    [HttpPut("{userId:guid}/assignment")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [Consumes("application/json")]
    [ProducesResponseType<StaffMemberResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<StaffMemberResponse>> Assign(Guid userId, [FromBody] AssignStaffRequest request, CancellationToken cancellationToken) =>
        Ok(await staffService.AssignAsync(userId, request, cancellationToken));

    /// <summary>Ends a staff member's assignment (the history is kept).</summary>
    [HttpDelete("{userId:guid}/assignment")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unassign(Guid userId, CancellationToken cancellationToken)
    {
        await staffService.UnassignAsync(userId, cancellationToken);
        return NoContent();
    }

    /// <summary>Where the signed-in staff member works, or null when they are not assigned yet.</summary>
    [HttpGet("me/assignment")]
    [Authorize(Roles = Roles.StaffRolesCsv)]
    [ProducesResponseType<MyAssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<MyAssignmentResponse>> GetMine(CancellationToken cancellationToken) =>
        Ok(await staffService.GetMineAsync(cancellationToken));
}
