using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NepalParcel.Application.Auth;
using NepalParcel.Contracts.Auth;
using NepalParcel.Domain.Identity;

namespace NepalParcel.API.Controllers;

/// <summary>Staff account administration. SuperAdmin only.</summary>
[ApiController]
[Route("api/v1/admin/users")]
[Produces("application/json")]
[Authorize(Roles = Roles.SuperAdmin)]
public sealed class AdminUsersController(IUserAdministrationService administration) : ControllerBase
{
    /// <summary>Creates a staff account with one of the assignable staff roles.</summary>
    [HttpPost]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CurrentUserResponse>> CreateStaffUser(
        [FromBody] CreateStaffUserRequest request,
        CancellationToken cancellationToken)
    {
        var created = await administration.CreateStaffUserAsync(request, cancellationToken);
        return Created($"/api/v1/admin/users/{created.Id}", created);
    }
}
