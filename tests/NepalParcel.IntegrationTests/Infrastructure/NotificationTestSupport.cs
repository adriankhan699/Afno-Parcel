using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NepalParcel.Application.Notifications;
using NepalParcel.Contracts.Notifications;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Notifications;
using NepalParcel.Infrastructure.Persistence;

namespace NepalParcel.IntegrationTests.Infrastructure;

/// <summary>The API with a WhatsApp app secret and verify token set, so the Meta webhook can be exercised. Providers are the mocks.</summary>
public class NotificationApiFactory : NepalParcelApiFactory
{
    public const string AppSecret = "integration-test-app-secret";
    public const string VerifyToken = "integration-test-verify-token";

    public NotificationApiFactory()
    {
        Settings["Notifications:WhatsApp:AppSecret"] = AppSecret;
        Settings["Notifications:WhatsApp:VerifyToken"] = VerifyToken;
        Settings["Notifications:Outbox:MaxAttempts"] = "3";
    }
}

/// <summary>
/// A provider whose answers the test decides: it replays a script of results, then accepts everything. It is registered as the
/// "Mock" provider for both channels, so the application chooses it exactly as it would the built-in mock.
/// </summary>
public sealed class ScriptedProvider(NotificationChannel channel) : IMessageProvider
{
    private readonly ConcurrentQueue<SendResult> _script = new();
    private readonly ConcurrentQueue<Exception> _throws = new();
    private int _counter;

    public ConcurrentQueue<OutboundMessage> Sent { get; } = new();

    public NotificationChannel Channel => channel;

    public string Name => channel == NotificationChannel.WhatsApp ? "ScriptedWhatsApp" : "ScriptedSms";

    public string ConfigurationName => "Mock";

    public bool IsMock => true;

    public bool IsConfigured => true;

    public string Description => "Scripted provider for tests.";

    public void Then(params SendResult[] results)
    {
        foreach (var result in results)
        {
            _script.Enqueue(result);
        }
    }

    /// <summary>The next send throws instead of answering, as a broken gateway client might.</summary>
    public void ThrowOnce(Exception exception) => _throws.Enqueue(exception);

    public Task<SendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Enqueue(message);
        if (_throws.TryDequeue(out var exception))
        {
            throw exception;
        }

        return Task.FromResult(_script.TryDequeue(out var next) ? next : SendResult.Accepted($"scripted-{Name}-{Interlocked.Increment(ref _counter)}"));
    }
}

public sealed class ScriptedProviderFactory : NotificationApiFactory
{
    public ScriptedProvider Sms { get; } = new(NotificationChannel.Sms);

    public ScriptedProvider WhatsApp { get; } = new(NotificationChannel.WhatsApp);

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.RemoveAll<IMessageProvider>();
        services.AddSingleton<IMessageProvider>(Sms);
        services.AddSingleton<IMessageProvider>(WhatsApp);
    }
}

internal static class NotificationTestSupport
{
    /// <summary>Runs the outbox processor until nothing is due, as the background worker would.</summary>
    public static async Task<OutboxRunResult> ProcessAllAsync(NepalParcelApiFactory factory)
    {
        int claimed = 0, submitted = 0, retrying = 0, failed = 0, skipped = 0;
        for (var round = 0; round < 40; round++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessDueAsync();
            if (result.Claimed == 0)
            {
                break;
            }

            claimed += result.Claimed;
            submitted += result.Submitted;
            retrying += result.Retrying;
            failed += result.Failed;
            skipped += result.Skipped;
        }

        return new OutboxRunResult(claimed, submitted, retrying, failed, skipped);
    }

    public static async Task<List<NotificationOutboxMessage>> MessagesAsync(NepalParcelApiFactory factory, Guid parcelId, string? templateKey = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var query = db.NotificationOutbox.AsNoTracking().Where(m => m.ParcelId == parcelId);
        if (templateKey is not null)
        {
            query = query.Where(m => m.TemplateKey == templateKey);
        }

        return await query.ToListAsync();
    }

    public static async Task<NotificationOutboxMessage> MessageAsync(
        NepalParcelApiFactory factory,
        Guid parcelId,
        NotificationAudience audience,
        NotificationChannel channel,
        string templateKey = NotificationTemplateKeys.ParcelBooked)
    {
        var all = await MessagesAsync(factory, parcelId, templateKey);
        return Assert.Single(all, m => m.Audience == audience && m.Channel == channel);
    }

    public static async Task<List<NotificationLog>> LogsAsync(NepalParcelApiFactory factory, Guid messageId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        return (await db.NotificationLogs.AsNoTracking().Where(l => l.OutboxMessageId == messageId).ToListAsync()).OrderBy(l => l.CreatedAtUtc).ThenBy(l => l.Id).ToList();
    }

    /// <summary>Makes a waiting retry due now, instead of sleeping through the backoff.</summary>
    public static async Task MakeDueAsync(NepalParcelApiFactory factory, Guid messageId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var message = await db.NotificationOutbox.SingleAsync(m => m.Id == messageId);
        db.Entry(message).Property(m => m.NextAttemptAtUtc).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    public static async Task<HttpClient> AdminAsync(NepalParcelApiFactory factory) => await TestUsers.CreateClientAsync(factory, Roles.SuperAdmin);

    /// <summary>The signature Meta would put in <c>X-Hub-Signature-256</c>.</summary>
    public static string MetaSignature(string body, string secret = NotificationApiFactory.AppSecret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    public static async Task<HttpResponseMessage> PostMetaAsync(HttpClient client, string body, string? signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/webhooks/whatsapp", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
        }

        return await client.SendAsync(request);
    }

    public static async Task<WebhookAcceptedResponse> SimulateAsync(HttpClient admin, string providerMessageId, string status, string? error = null)
    {
        var response = await admin.PostAsJsonAsync(
            new Uri($"/api/v1/notifications/mock/messages/{providerMessageId}/status", UriKind.Relative),
            new SimulateStatusRequest(status, error));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WebhookAcceptedResponse>())!;
    }
}
