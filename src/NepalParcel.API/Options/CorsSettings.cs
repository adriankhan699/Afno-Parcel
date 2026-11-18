namespace NepalParcel.API.Options;

/// <summary>Origins allowed to call the API from a browser (the React frontend).</summary>
internal sealed class CorsSettings
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = [];
}
