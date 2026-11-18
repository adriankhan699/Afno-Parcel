using System.ComponentModel.DataAnnotations;

namespace NepalParcel.Infrastructure.Persistence;

/// <summary>
/// Database settings bound from the <c>Database</c> section. The connection string itself comes from
/// <c>ConnectionStrings:DefaultConnection</c> (user-secrets, environment variable or secret store — never appsettings).
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public const string ConnectionStringName = "DefaultConnection";

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Applies pending EF Core migrations at startup. Intended for development/Docker only.</summary>
    public bool ApplyMigrationsOnStartup { get; set; }

    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    [Range(0, 10)]
    public int MaxRetryCount { get; set; } = 5;

    /// <summary>Logs parameter values in SQL. Never enable outside local development.</summary>
    public bool EnableSensitiveDataLogging { get; set; }
}
