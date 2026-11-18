using NepalParcel.API.Middleware;
using NepalParcel.Application.Common.Interfaces;

namespace NepalParcel.API.Services;

/// <summary>Resolves the current user from the validated JWT (claim types are not remapped: "sub", "name", "role").</summary>
internal sealed class HttpCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private const string Anonymous = "anonymous";

    public Guid? UserId =>
        Guid.TryParse(httpContextAccessor.HttpContext?.User.FindFirst("sub")?.Value, out var id) ? id : null;

    public string? UserName => httpContextAccessor.HttpContext?.User.FindFirst("name")?.Value ?? Anonymous;

    public bool IsAuthenticated => httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;
}

internal sealed class HttpClientInfo(IHttpContextAccessor httpContextAccessor) : IClientInfo
{
    public string? IpAddress => httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent
    {
        get
        {
            var value = httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString();
            return string.IsNullOrEmpty(value) ? null : value.Length > 256 ? value[..256] : value;
        }
    }

    public string? CorrelationId =>
        httpContextAccessor.HttpContext?.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var id) == true ? id as string : null;
}
