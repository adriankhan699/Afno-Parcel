namespace NepalParcel.Contracts.Common;

/// <summary>Paging parameters accepted by list endpoints.</summary>
public sealed record PageQuery
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>1-based page number. Defaults to 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Items per page (1–100). Defaults to 20.</summary>
    public int PageSize { get; init; } = DefaultPageSize;
}
