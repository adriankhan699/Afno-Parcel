namespace NepalParcel.Application.Common.Interfaces;

/// <summary>The identity performing the current operation (from the validated JWT).</summary>
public interface ICurrentUser
{
    /// <summary>Id of the authenticated user, or null when anonymous.</summary>
    Guid? UserId { get; }

    /// <summary>Display name, or "anonymous".</summary>
    string? UserName { get; }

    bool IsAuthenticated { get; }

    /// <summary>Whether the signed-in user has the role. Always false when anonymous.</summary>
    bool IsInRole(string role);
}

/// <summary>Information about the calling client, used for audit trails.</summary>
public interface IClientInfo
{
    string? IpAddress { get; }

    string? UserAgent { get; }

    string? CorrelationId { get; }
}
