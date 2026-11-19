using NepalParcel.API.Extensions;
using NepalParcel.Application;
using NepalParcel.Infrastructure;
using NepalParcel.Infrastructure.Identity;
using NepalParcel.Infrastructure.Persistence;
using Serilog;

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.AddStructuredLogging();

    builder.Services
        .AddApplication()
        .AddInfrastructure()
        .AddApiServices(builder.Configuration);

    var app = builder.Build();

    app.UseApiPipeline();

    await app.Services.InitializeDatabaseAsync();
    await app.Services.SeedReferenceDataAsync();
    await app.Services.EnsureSuperAdminAsync();
    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException && !string.Equals(ex.GetType().Name, "StopTheHostException", StringComparison.Ordinal))
{
    // Last-chance logging for startup failures (e.g. missing configuration).
    Log.Fatal(ex, "NepalParcel API terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Entry point marker, exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
