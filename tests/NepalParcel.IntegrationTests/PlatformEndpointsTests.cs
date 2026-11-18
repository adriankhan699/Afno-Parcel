using System.Net;
using System.Text.Json;
using NepalParcel.IntegrationTests.Infrastructure;

namespace NepalParcel.IntegrationTests;

public sealed class PlatformEndpointsTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task HealthLive_ReturnsOk()
    {
        var response = await _client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_ReportsDatabaseHealthy()
    {
        var response = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal("database", body.GetProperty("checks")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task SwaggerDocument_IsServed_AndDescribesBranches()
    {
        var response = await _client.GetAsync(new Uri("/swagger/v1/swagger.json", UriKind.Relative));
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/api/v1/branches", json, StringComparison.Ordinal);
        Assert.Contains("Afno Parcel Delivery API", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorrelationId_IsEchoed_WhenSupplied()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        request.Headers.Add("X-Correlation-ID", "test-correlation-123");

        var response = await _client.SendAsync(request);

        Assert.Equal("test-correlation-123", response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task CorrelationId_IsGenerated_WhenUnsafeValueSupplied()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", "bad value<script>");

        var response = await _client.SendAsync(request);
        var echoed = response.Headers.GetValues("X-Correlation-ID").Single();

        Assert.NotEqual("bad value<script>", echoed);
        Assert.Equal(32, echoed.Length);
    }

    [Fact]
    public async Task UnknownRoute_ReturnsProblemDetails404()
    {
        var response = await _client.GetAsync(new Uri("/api/v1/does-not-exist", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
