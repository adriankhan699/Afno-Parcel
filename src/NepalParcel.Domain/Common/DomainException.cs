namespace NepalParcel.Domain.Common;

/// <summary>
/// Raised when a business invariant is violated inside the domain model.
/// </summary>
public class DomainException : Exception
{
    public DomainException()
    {
    }

    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
