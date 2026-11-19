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

    /// <summary>
    /// Inserts the default service areas (all 77 districts) and development pricing rules when those tables are empty.
    /// Intended for development and demos; production prices should be entered through the pricing API.
    /// </summary>
    public bool SeedReferenceData { get; set; }

    /// <summary>
    /// Adds the default WhatsApp and SMS message texts that are missing (never overwriting edited ones). On by default because
    /// nothing can be sent without them; turn it off only if the database is prepared some other way.
    /// </summary>
    public bool SeedNotificationTemplates { get; set; } = true;

    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    [Range(0, 10)]
    public int MaxRetryCount { get; set; } = 5;

    /// <summary>Logs parameter values in SQL. Never enable outside local development.</summary>
    public bool EnableSensitiveDataLogging { get; set; }
}
