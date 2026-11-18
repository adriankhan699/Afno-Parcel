using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using NepalParcel.API.ExceptionHandling;
using NepalParcel.API.Middleware;
using NepalParcel.API.Options;

namespace NepalParcel.API.Extensions;

internal static class ServiceCollectionExtensions
{
    public const string FrontendCorsPolicy = "Frontend";

    /// <summary>Registers HTTP-layer services: controllers, ProblemDetails, Swagger, CORS and health checks.</summary>
    public static IServiceCollection AddApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddControllers()
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = ctx =>
            {
                ctx.ProblemDetails.Instance ??= $"{ctx.HttpContext.Request.Method} {ctx.HttpContext.Request.Path}";
                ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
                if (ctx.HttpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var correlationId))
                {
                    ctx.ProblemDetails.Extensions["correlationId"] = correlationId;
                }
            });
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddHttpContextAccessor();
        services.AddTransient<CorrelationIdMiddleware>();
        services.AddJwtAuthentication(configuration);

        services.AddOptions<CorsSettings>()
            .Bind(configuration.GetSection(CorsSettings.SectionName));
        services.AddCors(options =>
        {
            var origins = configuration.GetSection(CorsSettings.SectionName).Get<CorsSettings>()?.AllowedOrigins ?? [];
            options.AddPolicy(FrontendCorsPolicy, policy => policy
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .WithExposedHeaders(CorrelationIdMiddleware.HeaderName));
        });

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Afno Parcel Delivery API",
                Version = "v1",
                Description = "Booking, tracking and operations API for the Afno Parcel courier network. Currency: NPR.",
            });

            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Paste the accessToken returned by /api/v1/auth/login (without the 'Bearer ' prefix).",
            });
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
            });

            foreach (var assembly in new[] { Assembly.GetExecutingAssembly(), typeof(Contracts.Common.PagedResponse<>).Assembly })
            {
                var xml = Path.Combine(AppContext.BaseDirectory, $"{assembly.GetName().Name}.xml");
                if (File.Exists(xml))
                {
                    options.IncludeXmlComments(xml);
                }
            }
        });

        return services;
    }
}
