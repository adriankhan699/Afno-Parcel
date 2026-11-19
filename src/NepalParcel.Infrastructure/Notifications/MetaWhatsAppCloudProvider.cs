using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Notifications;
using NepalParcel.Domain.Notifications;

namespace NepalParcel.Infrastructure.Notifications;

/// <summary>
/// Sends WhatsApp <b>template</b> messages through Meta's WhatsApp Business Cloud API
/// (<c>POST {base}/{version}/{phone-number-id}/messages</c>). A business may start a conversation only with a template that
/// Meta has approved, so the message is sent by the template's name with the placeholder values as its numbered body fields.
/// The access token is a secret and comes from configuration at the moment of the call; it is never logged or stored.
/// </summary>
/// <remarks>
/// This class was written from Meta's published request and response shapes and has <b>not</b> been run against Meta: no
/// Meta account or credentials were available. It is covered by tests with a fake HTTP handler only. Try it with a test
/// number from Meta's developer console before relying on it (docs/notifications.md).
/// A successful response means Meta <i>accepted</i> the message and gave it an id. Whether it reached the phone is reported
/// later, by webhook, and is never assumed here.
/// </remarks>
internal sealed class MetaWhatsAppCloudProvider(HttpClient httpClient, IOptions<NotificationOptions> options) : IWhatsAppProvider
{
    public const string ProviderName = "MetaWhatsApp";

    // Meta error codes for "slow down, try again" (throttling). Anything else in the 4xx range is a refusal that retrying will not fix.
    private static readonly HashSet<int> RetryableCodes = [4, 17, 32, 613, 80007, 130429, 131048, 131056, 133016];

    // Credentials that may be fixed while messages wait (an expired token): retried rather than given up on.
    private static readonly HashSet<int> CredentialCodes = [0, 190, 102];

    public NotificationChannel Channel => NotificationChannel.WhatsApp;

    public string Name => ProviderName;

    public string ConfigurationName => WhatsAppOptions.MetaProvider;

    public bool IsMock => false;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.Value.WhatsApp.AccessToken) && !string.IsNullOrWhiteSpace(options.Value.WhatsApp.PhoneNumberId);

    public string Description => "Meta WhatsApp Business Cloud API.";

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var settings = options.Value.WhatsApp;
        if (!IsConfigured)
        {
            return SendResult.RetryLater("WhatsApp is not configured: the access token or phone number id is missing.");
        }

        if (string.IsNullOrWhiteSpace(message.ProviderTemplateName))
        {
            return SendResult.Rejected("The WhatsApp message has no approved template name.");
        }

        var url = new Uri($"{settings.BaseUrl.TrimEnd('/')}/{settings.ApiVersion}/{Uri.EscapeDataString(settings.PhoneNumberId!)}/messages", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(BuildBody(message)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return SendResult.RetryLater($"Meta could not be reached ({ex.GetType().Name}).");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SendResult.RetryLater("Meta did not answer in time.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Interpret(response.StatusCode, text);
        }
    }

    /// <summary>Turns Meta's answer into a result. Public to the tests, which feed it recorded shapes.</summary>
    internal static SendResult Interpret(HttpStatusCode status, string body)
    {
        var (id, code, detail) = ReadBody(body);
        if ((int)status is >= 200 and < 300)
        {
            return id is not null
                ? SendResult.Accepted(id)
                : SendResult.RetryLater("Meta answered successfully but gave no message id.");
        }

        var description = Describe(code, detail, status);
        if (status == HttpStatusCode.TooManyRequests || (int)status >= 500 || status == HttpStatusCode.RequestTimeout)
        {
            return SendResult.RetryLater(description);
        }

        if (status == HttpStatusCode.Unauthorized || (code is { } c && CredentialCodes.Contains(c)))
        {
            return SendResult.RetryLater($"{description} Check the access token.");
        }

        if (code is { } known && RetryableCodes.Contains(known))
        {
            return SendResult.RetryLater(description);
        }

        return SendResult.Rejected(description);
    }

    private static object BuildBody(OutboundMessage message) =>
        new
        {
            messaging_product = "whatsapp",
            to = message.To,
            type = "template",
            template = new
            {
                name = message.ProviderTemplateName,
                language = new { code = message.Language },
                components = message.Parameters.Count == 0
                    ? Array.Empty<object>()
                    : new object[]
                    {
                        new
                        {
                            type = "body",
                            parameters = message.Parameters.Select(p => new { type = "text", text = p }).ToArray(),
                        },
                    },
            },
        };

    private static (string? Id, int? Code, string? Detail) ReadBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            string? id = null;
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array
                && messages.GetArrayLength() > 0
                && messages[0].TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.String)
            {
                id = idElement.GetString();
            }

            int? code = null;
            string? detail = null;
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number && codeElement.TryGetInt32(out var parsed))
                {
                    code = parsed;
                }

                detail = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : null;
            }

            return (id, code, detail);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static string Describe(int? code, string? detail, HttpStatusCode status)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"Meta answered {(int)status}{(code is null ? string.Empty : $" (code {code})")}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}")}.");
        return text.Length <= 400 ? text : text[..400];
    }
}
