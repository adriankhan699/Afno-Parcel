namespace NepalParcel.Application.Common.Exceptions;

/// <summary>Base type for expected, client-facing application errors.</summary>
public abstract class AppException : Exception
{
    protected AppException(string message)
        : base(message)
    {
    }

    /// <summary>Short machine-friendly title, surfaced as ProblemDetails.title.</summary>
    public abstract string Title { get; }
}

/// <summary>The requested resource does not exist.</summary>
public sealed class NotFoundException : AppException
{
    public NotFoundException(string resource, object key)
        : base($"{resource} '{key}' was not found.")
    {
    }

    public override string Title => "Resource not found";
}

/// <summary>The request conflicts with the current state (e.g. duplicate unique value).</summary>
public sealed class ConflictException : AppException
{
    public ConflictException(string message)
        : base(message)
    {
    }

    public override string Title => "Conflict";
}

/// <summary>Someone else changed the same record first (optimistic concurrency). The client should reload and retry.</summary>
public sealed class ConcurrencyException : AppException
{
    public ConcurrencyException()
        : base("The record was modified by someone else. Reload and try again.")
    {
    }

    public override string Title => "Concurrency conflict";
}

/// <summary>Authentication failed. Messages are deliberately generic so they do not reveal whether an account exists.</summary>
public sealed class UnauthorizedException : AppException
{
    public UnauthorizedException(string message)
        : base(message)
    {
    }

    public override string Title => "Unauthorized";
}

/// <summary>The caller is signed in but their role does not allow this action.</summary>
public sealed class ForbiddenException : AppException
{
    public ForbiddenException(string message)
        : base(message)
    {
    }

    public override string Title => "Forbidden";
}

/// <summary>The request failed validation. Errors are keyed by property name.</summary>
public sealed class RequestValidationException : AppException
{
    public RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public override string Title => "Validation failed";
}
