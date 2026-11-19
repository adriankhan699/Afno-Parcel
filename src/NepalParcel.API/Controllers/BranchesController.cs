using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NepalParcel.Application.Branches;
using NepalParcel.Contracts.Branches;
using NepalParcel.Contracts.Common;
using NepalParcel.Domain.Identity;

namespace NepalParcel.API.Controllers;

/// <summary>Branches and the head office of the delivery network.</summary>
[ApiController]
[Route("api/v1/branches")]
[Produces("application/json")]
public sealed class BranchesController(IBranchService branchService) : ControllerBase
{
    /// <summary>
    /// Lists branches, ordered by name. Anyone sees the active ones; super administrators and branch managers can also include
    /// the inactive ones.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<PagedResponse<BranchResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResponse<BranchResponse>>> GetBranches(
        [FromQuery] BranchQuery query,
        CancellationToken cancellationToken) =>
        Ok(await branchService.GetPageAsync(query, cancellationToken));

    /// <summary>Gets a branch by id.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<BranchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BranchResponse>> GetBranch(Guid id, CancellationToken cancellationToken) =>
        Ok(await branchService.GetByIdAsync(id, cancellationToken));

    /// <summary>Registers a new branch or head office. SuperAdmin and BranchManager only.</summary>
    [HttpPost]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [Consumes("application/json")]
    [ProducesResponseType<BranchResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BranchResponse>> CreateBranch(
        [FromBody] CreateBranchRequest request,
        CancellationToken cancellationToken)
    {
        var created = await branchService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetBranch), new { id = created.Id }, created);
    }

    /// <summary>Changes a branch's name, address, phone number and email. A branch manager can only change their own branch.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [Consumes("application/json")]
    [ProducesResponseType<BranchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BranchResponse>> UpdateBranch(Guid id, [FromBody] UpdateBranchRequest request, CancellationToken cancellationToken) =>
        Ok(await branchService.UpdateAsync(id, request, cancellationToken));

    /// <summary>Switches a branch back on.</summary>
    [HttpPost("{id:guid}/activate")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<BranchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BranchResponse>> Activate(Guid id, CancellationToken cancellationToken) =>
        Ok(await branchService.ActivateAsync(id, cancellationToken));

    /// <summary>
    /// Switches a branch off. Refused (422) while it still operates active warehouses, has open parcels, or has staff assigned.
    /// </summary>
    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Roles = Roles.NetworkAdminsCsv)]
    [ProducesResponseType<BranchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BranchResponse>> Deactivate(Guid id, CancellationToken cancellationToken) =>
        Ok(await branchService.DeactivateAsync(id, cancellationToken));
}
