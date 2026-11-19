using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NepalParcel.API.Options;
using NepalParcel.Application.Parcels;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Identity;

namespace NepalParcel.API.Controllers;

/// <summary>Price quotes, the places we deliver to, and the pricing rules behind every delivery charge.</summary>
[ApiController]
[Produces("application/json")]
public sealed class PricingController(IPricingService pricingService) : ControllerBase
{
    /// <summary>Quotes the delivery charge and estimated delivery time for a parcel without booking it. No sign-in needed.</summary>
    [HttpPost("api/v1/pricing/quote")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.BookingPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<QuoteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<QuoteResponse>> Quote([FromBody] QuoteRequest request, CancellationToken cancellationToken) =>
        Ok(await pricingService.QuoteAsync(request, cancellationToken));

    /// <summary>Lists every province/district we deliver to, with typical delivery times. No sign-in needed.</summary>
    [HttpGet("api/v1/service-areas")]
    [AllowAnonymous]
    [ProducesResponseType<IReadOnlyList<ServiceAreaResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ServiceAreaResponse>>> GetServiceAreas(CancellationToken cancellationToken) =>
        Ok(await pricingService.GetServiceAreasAsync(cancellationToken));

    /// <summary>Lists pricing rules (SuperAdmin, BranchManager and Accountant).</summary>
    [HttpGet("api/v1/pricing/rules")]
    [Authorize(Roles = $"{Roles.SuperAdmin},{Roles.BranchManager},{Roles.Accountant}")]
    [ProducesResponseType<PagedResponse<PricingRuleResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<PricingRuleResponse>>> GetRules([FromQuery] PageQuery query, CancellationToken cancellationToken) =>
        Ok(await pricingService.GetRulesAsync(query, cancellationToken));

    /// <summary>Adds a pricing rule (SuperAdmin only). To change a price, deactivate the old rule and add a new one.</summary>
    [HttpPost("api/v1/pricing/rules")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [Consumes("application/json")]
    [ProducesResponseType<PricingRuleResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PricingRuleResponse>> CreateRule([FromBody] CreatePricingRuleRequest request, CancellationToken cancellationToken)
    {
        var created = await pricingService.CreateRuleAsync(request, cancellationToken);
        return Created(new Uri($"/api/v1/pricing/rules/{created.Id}", UriKind.Relative), created);
    }

    /// <summary>Switches a pricing rule off from now on (SuperAdmin only). Existing bookings keep the price they were given.</summary>
    [HttpPost("api/v1/pricing/rules/{id:guid}/deactivate")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeactivateRule(Guid id, CancellationToken cancellationToken)
    {
        await pricingService.DeactivateRuleAsync(id, cancellationToken);
        return NoContent();
    }
}
