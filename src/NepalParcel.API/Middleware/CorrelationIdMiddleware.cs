using Serilog.Context;

namespace NepalParcel.API.Middleware;

/// <summary>
/// Reads (or creates) an <c>X-Correlation-ID</c> per request, echoes it in the response and
/// pushes it into the Serilog log context so every log line for the request can be correlated.
/// </summary>
internal sealed class CorrelationIdMiddleware : IMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var values) &&
                            IsSafe(values.ToString())
            ? values.ToString()
            : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    // Reject oversized or unusual values so clients cannot inject arbitrary content into logs/headers.
    private static bool IsSafe(string value) =>
        value.Length is > 0 and <= MaxLength &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
