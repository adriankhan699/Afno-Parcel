using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Notifications;
using NepalParcel.Domain.Notifications;
using NepalParcel.Infrastructure.Notifications;

namespace NepalParcel.IntegrationTests;

/// <summary>
/// The Meta WhatsApp Cloud API client, against a fake HTTP server. These check what we send and how every kind of answer is
/// understood. They do not prove that Meta accepts the request: no Meta account was available, so the shapes come from Meta's
/// published documentation and have to be tried with a test number from Meta's console before going live.
/// </summary>
public sealed class MetaWhatsAppProviderTests
{
    private const string Token = "EAAB-super-secret-access-token";

    private static OutboundMessage Message(params string[] parameters) =>
        new(NotificationChannel.WhatsApp, "9779851234567", NotificationTemplateKeys.ParcelShipped, "en", "text", "afno_parcel_shipped", parameters, "key-1");

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await respond(request);
        }
    }

    private static (MetaWhatsAppCloudProvider Provider, FakeHandler Handler) Create(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond,
        Action<WhatsAppOptions>? configure = null)
    {
        var whatsApp = new WhatsAppOptions { Provider = "Meta", PhoneNumberId = "106540352242922", AccessToken = Token, AppSecret = "s", VerifyToken = "v" };
        configure?.Invoke(whatsApp);
        var handler = new FakeHandler(respond);
        return (new MetaWhatsAppCloudProvider(new HttpClient(handler), Options.Create(new NotificationOptions { WhatsApp = whatsApp })), handler);
    }

    private static Task<HttpResponseMessage> Reply(HttpStatusCode status, string body) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    private static string Error(int code, string message) =>
        JsonSerializer.Serialize(new { error = new { message, type = "OAuthException", code, fbtrace_id = "ABC" } });

    // ---- what is sent ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATemplateMessage_IsPostedToThePhoneNumbersMessagesEndpoint_WithTheBearerToken()
    {
        var (provider, handler) = Create(_ => Reply(HttpStatusCode.OK, """{"messaging_product":"whatsapp","contacts":[{"input":"9779851234567","wa_id":"9779851234567"}],"messages":[{"id":"wamid.HBgL123"}]}"""));

        var result = await provider.SendAsync(Message("NP-1", "https://x.test/track/NP-1"));

        Assert.Equal(SendOutcome.Accepted, result.Outcome);
        Assert.Equal("wamid.HBgL123", result.ProviderMessageId);
        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://graph.facebook.com/v21.0/106540352242922/messages", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("whatsapp", root.GetProperty("messaging_product").GetString());
        Assert.Equal("9779851234567", root.GetProperty("to").GetString());
        Assert.Equal("template", root.GetProperty("type").GetString());
        var template = root.GetProperty("template");
        Assert.Equal("afno_parcel_shipped", template.GetProperty("name").GetString());
        Assert.Equal("en", template.GetProperty("language").GetProperty("code").GetString());
        var component = Assert.Single(template.GetProperty("components").EnumerateArray());
        Assert.Equal("body", component.GetProperty("type").GetString());
        Assert.Equal(
            ["NP-1", "https://x.test/track/NP-1"],
            component.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("text").GetString()!).ToArray());
        Assert.All(component.GetProperty("parameters").EnumerateArray(), p => Assert.Equal("text", p.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task ATemplateWithNoFields_SendsNoBodyComponent()
    {
        var (provider, handler) = Create(_ => Reply(HttpStatusCode.OK, """{"messages":[{"id":"wamid.X"}]}"""));

        await provider.SendAsync(Message());

        using var json = JsonDocument.Parse(handler.Calls.Single().Body);
        Assert.Equal(0, json.RootElement.GetProperty("template").GetProperty("components").GetArrayLength());
    }

    [Fact]
    public async Task TheVersionAndBaseAddress_ComeFromConfiguration()
    {
        var (provider, handler) = Create(
            _ => Reply(HttpStatusCode.OK, """{"messages":[{"id":"wamid.X"}]}"""),
            o =>
            {
                o.ApiVersion = "v23.0";
                o.BaseUrl = "https://graph.example.test/";
            });

        await provider.SendAsync(Message("a"));

        Assert.Equal("https://graph.example.test/v23.0/106540352242922/messages", handler.Calls.Single().Request.RequestUri!.ToString());
    }

    // ---- what the answers mean ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AcceptedByMeta_MeansSubmitted_NothingMore()
    {
        var (provider, _) = Create(_ => Reply(HttpStatusCode.OK, """{"messages":[{"id":"wamid.ONLYSUBMITTED"}]}"""));

        var result = await provider.SendAsync(Message("a"));

        // The result has an id and nothing that could be mistaken for a delivery.
        Assert.Equal(new SendResult(SendOutcome.Accepted, "wamid.ONLYSUBMITTED", null), result);
    }

    [Fact]
    public async Task ASuccessfulAnswerWithoutAnId_IsNotTreatedAsAcceptance()
    {
        var (provider, _) = Create(_ => Reply(HttpStatusCode.OK, """{"messaging_product":"whatsapp"}"""));

        var result = await provider.SendAsync(Message("a"));

        Assert.Equal(SendOutcome.RetryLater, result.Outcome);
        Assert.Null(result.ProviderMessageId);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 131026, SendOutcome.Rejected)]
    [InlineData(HttpStatusCode.BadRequest, 132001, SendOutcome.Rejected)]
    [InlineData(HttpStatusCode.BadRequest, 132000, SendOutcome.Rejected)]
    [InlineData(HttpStatusCode.BadRequest, 100, SendOutcome.Rejected)]
    [InlineData(HttpStatusCode.BadRequest, 130429, SendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.BadRequest, 131056, SendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.BadRequest, 80007, SendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.Forbidden, 4, SendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.Unauthorized, 190, SendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.BadRequest, 190, SendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.TooManyRequests, 130429, SendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.InternalServerError, 1, SendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.BadGateway, 2, SendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 2, SendOutcome.RetryLater)]
    public async Task EveryKindOfError_IsEitherWorthRetryingOrNot(HttpStatusCode status, int code, SendOutcome expected)
    {
        var (provider, _) = Create(_ => Reply(status, Error(code, "Something went wrong")));

        var result = await provider.SendAsync(Message("a"));

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.ProviderMessageId);
        Assert.Contains(code.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Error, StringComparison.Ordinal);
        Assert.Contains("Something went wrong", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExpiredToken_ExplainsWhere_AndIsRetriedSoItRecoversOnceFixed()
    {
        var (provider, _) = Create(_ => Reply(HttpStatusCode.Unauthorized, Error(190, "Error validating access token: Session has expired")));

        var result = await provider.SendAsync(Message("a"));

        Assert.Equal(SendOutcome.RetryLater, result.Outcome);
        Assert.Contains("access token", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAnswerThatIsNotJson_IsRetried()
    {
        var (provider, _) = Create(_ => Reply(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));

        var result = await provider.SendAsync(Message("a"));

        Assert.Equal(SendOutcome.RetryLater, result.Outcome);
        Assert.Contains("502", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANetworkError_IsRetried()
    {
        var (provider, _) = Create(_ => throw new HttpRequestException("no route to host"));

        var result = await provider.SendAsync(Message("a"));

        Assert.Equal(SendOutcome.RetryLater, result.Outcome);
        Assert.Contains("could not be reached", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeout_IsRetried()
    {
        var (provider, _) = Create(_ => throw new TaskCanceledException("timed out"));

        var result = await provider.SendAsync(Message("a"));

        Assert.Equal(SendOutcome.RetryLater, result.Outcome);
        Assert.Contains("in time", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShuttingDown_IsNotMistakenForATimeout()
    {
        using var cts = new CancellationTokenSource();
        var (provider, _) = Create(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException("cancelled");
        });

        await Assert.ThrowsAsync<TaskCanceledException>(() => provider.SendAsync(Message("a"), cts.Token));
    }

    // ---- configuration and secrets -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "106540352242922")]
    [InlineData("", "106540352242922")]
    [InlineData(Token, null)]
    [InlineData(Token, " ")]
    public async Task WithoutCredentials_NothingIsSent_AndTheMessageIsRetriedUntilTheyAreSet(string? token, string? phoneNumberId)
    {
        var (provider, handler) = Create(
            _ => Reply(HttpStatusCode.OK, """{"messages":[{"id":"wamid.X"}]}"""),
            o =>
            {
                o.AccessToken = token;
                o.PhoneNumberId = phoneNumberId;
            });

        var result = await provider.SendAsync(Message("a"));

        Assert.False(provider.IsConfigured);
        Assert.Equal(SendOutcome.RetryLater, result.Outcome);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ATemplateWithoutAnApprovedName_IsRefusedWithoutCallingMeta()
    {
        var (provider, handler) = Create(_ => Reply(HttpStatusCode.OK, """{"messages":[{"id":"wamid.X"}]}"""));

        var result = await provider.SendAsync(Message("a") with { ProviderTemplateName = " " });

        Assert.Equal(SendOutcome.Rejected, result.Outcome);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task TheAccessToken_NeverAppearsInAResultOrAnError()
    {
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError })
        {
            var (provider, _) = Create(_ => Reply(status, status == HttpStatusCode.OK ? """{"messages":[{"id":"wamid.X"}]}""" : Error(190, "bad")));

            var result = await provider.SendAsync(Message("a"));

            Assert.DoesNotContain(Token, result.Error ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, result.ProviderMessageId ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheProviderIsIdentifiedAsMeta_AndIsNotAMock()
    {
        var (provider, _) = Create(_ => Reply(HttpStatusCode.OK, "{}"));

        Assert.Equal("MetaWhatsApp", provider.Name);
        Assert.Equal("Meta", provider.ConfigurationName);
        Assert.False(provider.IsMock);
        Assert.Equal(NotificationChannel.WhatsApp, provider.Channel);
        Assert.True(provider.IsConfigured);
    }
}
