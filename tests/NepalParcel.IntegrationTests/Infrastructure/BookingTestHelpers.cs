using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Auth;
using NepalParcel.Contracts.Branches;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Infrastructure.Persistence;

namespace NepalParcel.IntegrationTests.Infrastructure;

/// <summary>Shared building blocks for the booking, pricing and parcel endpoint tests.</summary>
internal sealed class BookingTestHelpers(NepalParcelApiFactory factory)
{
    public static readonly Uri ParcelsUri = new("/api/v1/parcels", UriKind.Relative);
    public static readonly Uri QuoteUri = new("/api/v1/pricing/quote", UriKind.Relative);

    public static AddressDto KathmanduAddress { get; } = new("Bagmati", "Kathmandu", "Kathmandu Metropolitan City", 10, "New Baneshwor");

    public static AddressDto LalitpurAddress { get; } = new("Bagmati", "Lalitpur", "Lalitpur Metropolitan City", 3, "Pulchowk");

    public static AddressDto PokharaAddress { get; } = new("Gandaki", "Kaski", "Pokhara Metropolitan City", 8, "Lakeside");

    public static AddressDto DarchulaAddress { get; } = new("Sudurpashchim", "Darchula", "Mahakali Municipality", 4, "Bazaar");

    /// <summary>A random but valid Nepali mobile number, so searches by phone are unambiguous.</summary>
    public static string NewPhone() => "98" + Random.Shared.Next(10_000_000, 99_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    public async Task<HttpClient> StaffAsync(string role = Roles.BookingStaff) => await TestUsers.CreateClientAsync(factory, role);

    /// <summary>Registers a real customer account over HTTP (which also creates the customer profile) and signs in.</summary>
    public async Task<HttpClient> CustomerAsync()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/register", UriKind.Relative),
            new RegisterRequest("Ram Sharma", TestUsers.NewEmail("customer"), "9841234567", TestUsers.NewPassword()));
        response.EnsureSuccessStatusCode();

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    /// <summary>Creates a branch in Kathmandu (which has a service area) and returns its id.</summary>
    public async Task<Guid> CreateBranchAsync(string address = "Kathmandu")
    {
        var manager = await TestUsers.CreateClientAsync(factory, Roles.BranchManager);
        var location = address == "Kathmandu"
            ? KathmanduAddress
            : new AddressDto("Gandaki", "Kaski", "Pokhara Metropolitan City", 8, "Lakeside");
        var response = await manager.PostAsJsonAsync(
            new Uri("/api/v1/branches", UriKind.Relative),
            new CreateBranchRequest("T" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), "Test Branch", "Branch", location, "01-5523456"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BranchResponse>())!.Id;
    }

    public static BookParcelRequest NewBooking(
        Guid originBranchId,
        string service = "Standard",
        string payment = "Cash",
        decimal weight = 2.5m,
        decimal? cod = null,
        AddressDto? receiverAddress = null,
        string? receiverName = null,
        string? receiverPhone = null,
        string? senderName = null) =>
        new(
            new BookingPartyDto(senderName ?? "Ram Sharma", "9841234567", KathmanduAddress, "Call before arriving"),
            new BookingPartyDto(receiverName ?? "Sita Gurung", receiverPhone ?? "9851234567", receiverAddress ?? PokharaAddress, "Blue gate"),
            originBranchId,
            "Books and clothes",
            2,
            weight,
            service,
            payment,
            CodAmount: cod,
            PaymentReference: "REF-1");

    public static QuoteRequest NewQuote(Guid originBranchId, string service = "Standard", decimal weight = 2.5m, decimal? cod = null, AddressDto? destination = null) =>
        new(originBranchId, destination ?? PokharaAddress, service, weight, CodAmount: cod);

    public static async Task<ParcelResponse> BookAsync(HttpClient client, BookParcelRequest request)
    {
        var response = await client.PostAsJsonAsync(ParcelsUri, request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, $"Booking failed with {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<ParcelResponse>(body, JsonSerializerOptions.Web)!;
    }

    public static StringContent JsonWith(object request, params (string Name, JsonNode? Value)[] extra)
    {
        var node = JsonSerializer.SerializeToNode(request, JsonSerializerOptions.Web)!.AsObject();
        foreach (var (name, value) in extra)
        {
            node[name] = value;
        }

        return new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// Moves a parcel along its journey directly in the database, supplying the branch, warehouse and remarks each step needs.
    /// For tests that need a parcel in some state without caring how it got there.
    /// </summary>
    public async Task AdvanceAsync(Guid parcelId, params ParcelStatusCode[] steps)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var parcel = await db.Parcels.Include(p => p.StatusHistory).SingleAsync(p => p.Id == parcelId);
        Guid? warehouseId = null;
        foreach (var step in steps)
        {
            var rule = ParcelStatusRules.LocationFor(step);
            Guid? branch = rule is LocationRule.Branch or LocationRule.BranchOrWarehouse ? parcel.OriginBranchId : null;
            Guid? warehouse = null;
            if (rule == LocationRule.Warehouse)
            {
                warehouseId ??= await EnsureWarehouseAsync(db, parcel.OriginBranchId);
                warehouse = warehouseId;
            }

            parcel.ChangeStatus(
                step,
                DateTimeOffset.UtcNow,
                null,
                "test",
                ParcelStatusRules.RequiresRemarks(step) ? "test remark" : null,
                branch,
                warehouse);
        }

        await db.SaveChangesAsync();
    }

    private static async Task<Guid> EnsureWarehouseAsync(NepalParcelDbContext db, Guid branchId)
    {
        var warehouse = NepalParcel.Domain.Warehouses.Warehouse.Create(
            branchId,
            "W" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            "Test Hub",
            NepalParcel.Domain.Common.NepalAddress.Create(NepalParcel.Domain.Common.Province.Bagmati, "Kathmandu", "Kathmandu Metropolitan City", 5),
            "9841000009");
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    /// <summary>Registers a warehouse over HTTP as a branch manager and returns its id.</summary>
    public async Task<Guid> CreateWarehouseAsync(Guid branchId, string name = "Kathmandu Hub")
    {
        var manager = await TestUsers.CreateClientAsync(factory, Roles.BranchManager);
        var response = await manager.PostAsJsonAsync(
            new Uri("/api/v1/warehouses", UriKind.Relative),
            new NepalParcel.Contracts.Warehouses.CreateWarehouseRequest(
                branchId, "W" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), name, KathmanduAddress, "01-4123456", 5000));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NepalParcel.Contracts.Warehouses.WarehouseResponse>())!.Id;
    }

    /// <summary>The newest one-time code that was queued for the parcel's receiver (what the SMS would contain).</summary>
    public async Task<string> LatestOtpCodeAsync(Guid parcelId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var message = (await db.NotificationOutbox.AsNoTracking().Where(m => m.ParcelId == parcelId && m.TemplateKey == "tracking-otp").ToListAsync())
            .OrderByDescending(m => m.CreatedAtUtc)
            .First();
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("code").GetString()!;
    }

    public static Uri StatusUri(Guid parcelId) => new($"/api/v1/parcels/{parcelId}/status", UriKind.Relative);

    public static UpdateParcelStatusRequest To(string status, string? remarks = null, Guid? branchId = null, Guid? warehouseId = null) =>
        new(status, remarks, branchId, warehouseId);

    public static string ProblemMediaType(HttpResponseMessage response) => response.Content.Headers.ContentType?.MediaType ?? string.Empty;
}
