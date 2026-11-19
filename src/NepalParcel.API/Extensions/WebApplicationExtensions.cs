using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NepalParcel.API.Middleware;
using Serilog;

namespace NepalParcel.API.Extensions;

internal static class WebApplicationExtensions
{
    /// <summary>Configures the HTTP request pipeline. Order matters.</summary>
    public static WebApplication UseApiPipeline(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        app.UseSerilogRequestLogging(options =>
        {
            options.EnrichDiagnosticContext = (diagnostics, http) =>
            {
                diagnostics.Set("RequestHost", http.Request.Host.Value ?? string.Empty);
                diagnostics.Set("UserAgent", http.Request.Headers.UserAgent.ToString());
            };
        });

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
        {
            app.UseSwagger();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/swagger/v1/swagger.json", "Afno Parcel API v1");
                options.DocumentTitle = "Afno Parcel API";
            });
        }

        app.UseCors(ServiceCollectionExtensions.FrontendCorsPolicy);

        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseBackgroundJobs();

        app.MapControllers();

        // Liveness: process is up. Readiness: dependencies (database) reachable.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteHealthResponse,
        });

        return app;
    }

    private static Task WriteHealthResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = e.Value.Duration.TotalMilliseconds,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web));
    }
}
