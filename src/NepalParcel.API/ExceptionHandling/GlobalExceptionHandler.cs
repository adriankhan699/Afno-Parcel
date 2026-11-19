using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Domain.Common;

namespace NepalParcel.API.ExceptionHandling;

/// <summary>
/// Converts exceptions into RFC 9457 ProblemDetails responses. Expected application errors map to
/// 4xx with their message; anything else is logged and returned as a generic 500 without internals.
/// </summary>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected; nothing useful to send back.
            httpContext.Response.StatusCode = 499;
            return true;
        }

        var problem = exception switch
        {
            RequestValidationException validation => new ValidationProblemDetails(
                validation.Errors.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = validation.Title,
                Detail = validation.Message,
            },
            NotFoundException notFound => Create(StatusCodes.Status404NotFound, notFound.Title, notFound.Message),
            ConflictException conflict => Create(StatusCodes.Status409Conflict, conflict.Title, conflict.Message),
            ConcurrencyException concurrency => Create(StatusCodes.Status409Conflict, concurrency.Title, concurrency.Message),
            UnauthorizedException unauthorized => Create(StatusCodes.Status401Unauthorized, unauthorized.Title, unauthorized.Message),
            ForbiddenException forbidden => Create(StatusCodes.Status403Forbidden, forbidden.Title, forbidden.Message),
            DomainException domain => Create(StatusCodes.Status422UnprocessableEntity, "Business rule violated", domain.Message),
            BadHttpRequestException badRequest => Create(badRequest.StatusCode, "Bad request", badRequest.Message),
            _ => Create(StatusCodes.Status500InternalServerError, "Server error", "An unexpected error occurred. Please try again later."),
        };

        if (problem.Status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            LogHandled(logger, exception.GetType().Name, problem.Status ?? 0, exception.Message);
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        if (problem.Status == StatusCodes.Status401Unauthorized)
        {
            httpContext.Response.Headers.WWWAuthenticate = "Bearer";
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static ProblemDetails Create(int status, string title, string detail) =>
        new() { Status = status, Title = title, Detail = detail };

    [LoggerMessage(EventId = 5000, Level = LogLevel.Error, Message = "Unhandled exception processing {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, PathString path);

    [LoggerMessage(EventId = 4000, Level = LogLevel.Warning, Message = "{ExceptionType} mapped to HTTP {StatusCode}: {Reason}")]
    private static partial void LogHandled(ILogger logger, string exceptionType, int statusCode, string reason);
}
