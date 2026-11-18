using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NepalParcel.Application.Branches;
using NepalParcel.Contracts.Branches;
using NepalParcel.Contracts.Common;
using NepalParcel.Domain.Identity;

namespace NepalParcel.API.Controllers;

/// <summary>Branches, head office and warehouses of the delivery network.</summary>
[ApiController]
[Route("api/v1/branches")]
[Produces("application/json")]
public sealed class BranchesController(IBranchService branchService) : ControllerBase
{
    /// <summary>Lists branches, ordered by name.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResponse<BranchResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResponse<BranchResponse>>> GetBranches(
        [FromQuery] PageQuery query,
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
    [Authorize(Roles = $"{Roles.SuperAdmin},{Roles.BranchManager}")]
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
}
