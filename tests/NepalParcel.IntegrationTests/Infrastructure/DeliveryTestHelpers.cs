using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Auth;
using NepalParcel.Contracts.Delivery;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Infrastructure.Persistence;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;

namespace NepalParcel.IntegrationTests.Infrastructure;

/// <summary>Building blocks for the delivery agent tests: agents with real logins, parcels ready for delivery, codes and pictures.</summary>
internal sealed class DeliveryTestHelpers(NepalParcelApiFactory factory)
{
    public static readonly Uri AgentsUri = new("/api/v1/delivery-agents", UriKind.Relative);
    public static readonly Uri ReadyUri = new("/api/v1/delivery/ready", UriKind.Relative);
    public static readonly Uri AssignmentsUri = new("/api/v1/delivery/assignments", UriKind.Relative);
    public static readonly Uri AgentMeUri = new("/api/v1/agent/me", UriKind.Relative);

    /// <summary>The first bytes of a PNG, which is all the server looks at to tell what kind of picture it is.</summary>
    public static byte[] PngBytes(int extra = 64) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[extra]];

    public static byte[] JpegBytes(int extra = 64) => [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[extra]];

    private readonly BookingTestHelpers _booking = new(factory);

    public static Uri AgentUri(Guid id, string suffix = "") => new($"/api/v1/delivery-agents/{id}{suffix}", UriKind.Relative);

    public static Uri AgentAssignmentUri(Guid id, string suffix = "") => new($"/api/v1/agent/assignments/{id}{suffix}", UriKind.Relative);

    public static Uri ManagerAssignmentUri(Guid id, string suffix = "") => new($"/api/v1/delivery/assignments/{id}{suffix}", UriKind.Relative);

    public Task<HttpClient> AdminAsync() => _booking.StaffAsync(Roles.SuperAdmin);

    /// <summary>Creates an agent through the API (as a super administrator, unless a client is given) and signs them in.</summary>
    public async Task<(HttpClient Client, DeliveryAgentResponse Agent, string Email, string Password)> NewAgentAsync(Guid branchId, HttpClient? creator = null)
    {
        creator ??= await AdminAsync();
        var email = TestUsers.NewEmail("agent");
        var password = TestUsers.NewPassword();
        var response = await creator.PostAsJsonAsync(
            AgentsUri,
            new CreateDeliveryAgentRequest("Agent Ram", email, NewPhone(), password, branchId, "Motorbike", "ba 1 pa 1234", "LIC-1"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, $"Creating the agent failed with {(int)response.StatusCode}: {body}");
        var agent = JsonSerializer.Deserialize<DeliveryAgentResponse>(body, JsonSerializerOptions.Web)!;
        return (await SignInAsync(email, password), agent, email, password);
    }

    public async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(new Uri("/api/v1/auth/login", UriKind.Relative), new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    /// <summary>A booked parcel that is sitting at a warehouse of the branch, ready to be given to an agent.</summary>
    public async Task<ParcelResponse> ReadyParcelAsync(Guid branchId, BookParcelRequest? booking = null)
    {
        var customer = await _booking.CustomerAsync();
        var parcel = await BookAsync(customer, booking ?? NewBooking(branchId));
        await _booking.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.InTransit, ParcelStatusCode.ArrivedAtWarehouse);
        return parcel;
    }

    public async Task<DeliveryAssignmentResponse> AssignAsync(HttpClient manager, Guid parcelId, Guid agentId)
    {
        var response = await manager.PostAsJsonAsync(AssignmentsUri, new AssignDeliveryRequest(parcelId, agentId));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, $"Assigning failed with {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<DeliveryAssignmentResponse>(body, JsonSerializerOptions.Web)!;
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success but got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;
    }

    public static async Task<AgentAssignmentResponse> AgentViewAsync(HttpClient agent, Guid assignmentId) =>
        await ReadAsync<AgentAssignmentResponse>(await agent.GetAsync(AgentAssignmentUri(assignmentId)));

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, Uri uri, object? body = null) =>
        client.PostAsJsonAsync(uri, body ?? new { });

    /// <summary>Assigns the parcel, takes it out for delivery and returns the agent's view of it.</summary>
    public async Task<DeliveryAssignmentResponse> StartedAsync(HttpClient manager, HttpClient agent, Guid parcelId, Guid agentId)
    {
        var assignment = await AssignAsync(manager, parcelId, agentId);
        (await agent.PostAsync(AgentAssignmentUri(assignment.Id, "/start"), null)).EnsureSuccessStatusCode();
        return assignment;
    }

    /// <summary>The newest one-time delivery code that was queued for the parcel's receiver (what the SMS would contain).</summary>
    public async Task<string> LatestDeliveryCodeAsync(Guid parcelId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var message = (await db.NotificationOutbox.AsNoTracking().Where(m => m.ParcelId == parcelId && m.TemplateKey == "delivery-otp").ToListAsync())
            .OrderByDescending(m => m.CreatedAtUtc)
            .First();
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("code").GetString()!;
    }

    public static MultipartFormDataContent FileForm(string type, byte[] bytes, string? recipientName = null, string contentType = "image/png", string fileName = "proof.png")
    {
        var form = new MultipartFormDataContent { { new StringContent(type), "type" } };
        if (recipientName is not null)
        {
            form.Add(new StringContent(recipientName), "recipientName");
        }

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return form;
    }

    /// <summary>Records an OTP proof the way an agent does: send the code, read it from the outbox, enter it.</summary>
    public async Task<AgentAssignmentResponse> ProveByCodeAsync(HttpClient agent, Guid assignmentId, Guid parcelId)
    {
        (await agent.PostAsync(AgentAssignmentUri(assignmentId, "/delivery-code"), null)).EnsureSuccessStatusCode();
        var code = await LatestDeliveryCodeAsync(parcelId);
        return await ReadAsync<AgentAssignmentResponse>(
            await agent.PostAsJsonAsync(AgentAssignmentUri(assignmentId, "/proof/otp"), new VerifyDeliveryCodeRequest(code, "Sita Gurung")));
    }

    public async Task<ParcelResponse> ParcelAsAdminAsync(Guid parcelId)
    {
        var admin = await AdminAsync();
        return await ReadAsync<ParcelResponse>(await admin.GetAsync(new Uri($"/api/v1/parcels/{parcelId}", UriKind.Relative)));
    }
}
