using Serilog;

namespace NepalParcel.API.Extensions;

internal static class LoggingExtensions
{
    /// <summary>
    /// Configures Serilog from the <c>Serilog</c> configuration section (sinks, levels, enrichers).
    /// Defaults to structured console output so container platforms can collect logs from stdout.
    /// </summary>
    public static WebApplicationBuilder AddStructuredLogging(this WebApplicationBuilder builder)
    {
        // Logs startup failures that happen before the host's logger is built.
        // Replaced by the configured logger once the host is built.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();

        builder.Services.AddSerilog((services, configuration) => configuration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "NepalParcel.API")
            .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName));

        return builder;
    }
}
