using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Branches;
using NepalParcel.Contracts.Common;
using NepalParcel.Domain.Identity;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.IntegrationTests.Infrastructure;

namespace NepalParcel.IntegrationTests;

public sealed class BranchesEndpointsTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>, IAsyncLifetime
{
    private static readonly Uri BranchesUri = new("/api/v1/branches", UriKind.Relative);
    private HttpClient _manager = null!;

    public async Task InitializeAsync() => _manager = await TestUsers.CreateClientAsync(factory, Roles.BranchManager);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Create_ThenGetById_RoundTripsBranch_WithoutLogin()
    {
        var request = NewRequest();

        var createResponse = await _manager.PostAsJsonAsync(BranchesUri, request);
        var created = await createResponse.Content.ReadFromJsonAsync<BranchResponse>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(createResponse.Headers.Location);
        Assert.NotNull(created);
        Assert.Equal(request.Code, created.Code);
        Assert.Equal("Bagmati", created.Address.Province);
        Assert.True(created.IsActive);
        Assert.NotEqual(default, created.CreatedAtUtc);

        // Branch locations are public information: anonymous visitors can read them.
        var anonymous = factory.CreateClient();
        var fetched = await anonymous.GetFromJsonAsync<BranchResponse>(createResponse.Headers.Location);
        Assert.Equal(created, fetched);
    }

    [Fact]
    public async Task Create_WithoutToken_Returns401Problem()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(BranchesUri, NewRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(Roles.Customer)]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.DeliveryAgent)]
    public async Task Create_WithoutPrivilegedRole_Returns403(string role)
    {
        var client = await TestUsers.CreateClientAsync(factory, role);

        var response = await client.PostAsJsonAsync(BranchesUri, NewRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_AsSuperAdmin_Succeeds()
    {
        var admin = await TestUsers.CreateClientAsync(factory, Roles.SuperAdmin);

        var response = await admin.PostAsJsonAsync(BranchesUri, NewRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_StampsAuditColumnsWithUserId_NotName()
    {
        // Created by an authenticated manager: CreatedBy is a user id (GUID), never the person's name or email.
        var response = await _manager.PostAsJsonAsync(BranchesUri, NewRequest());
        var created = await response.Content.ReadFromJsonAsync<BranchResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var branch = await db.Branches.AsNoTracking().SingleAsync(b => b.Id == created!.Id);

        Assert.True(Guid.TryParse(branch.CreatedBy, out _), $"CreatedBy should be a user id but was '{branch.CreatedBy}'.");
        Assert.DoesNotContain("@", branch.CreatedBy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_DuplicateCode_Returns409Problem()
    {
        var request = NewRequest();
        (await _manager.PostAsJsonAsync(BranchesUri, request)).EnsureSuccessStatusCode();

        var response = await _manager.PostAsJsonAsync(BranchesUri, request with { Code = request.Code.ToLowerInvariant() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Create_InvalidRequest_Returns400WithFieldErrors()
    {
        var request = NewRequest() with
        {
            PhoneNumber = "123",
            Address = NewRequest().Address with { Province = "Atlantis" },
        };

        var response = await _manager.PostAsJsonAsync(BranchesUri, request);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("PhoneNumber", out _));
        Assert.True(errors.TryGetProperty("Address.Province", out _));
        Assert.True(problem.TryGetProperty("traceId", out _));
        Assert.True(problem.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task Create_WarehouseType_IsRejected_BecauseWarehousesAreSeparate()
    {
        var response = await _manager.PostAsJsonAsync(BranchesUri, NewRequest() with { Type = "Warehouse" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetById_Unknown_Returns404Problem()
    {
        var response = await factory.CreateClient().GetAsync(new Uri($"/api/v1/branches/{Guid.NewGuid()}", UriKind.Relative));
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Resource not found", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task List_ReturnsPagedResults()
    {
        for (var i = 0; i < 3; i++)
        {
            (await _manager.PostAsJsonAsync(BranchesUri, NewRequest())).EnsureSuccessStatusCode();
        }

        var page = await factory.CreateClient().GetFromJsonAsync<PagedResponse<BranchResponse>>(
            new Uri("/api/v1/branches?page=1&pageSize=2", UriKind.Relative));

        Assert.NotNull(page);
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.TotalCount >= 3);
        Assert.True(page.HasNextPage);
    }

    [Fact]
    public async Task List_InvalidPageSize_Returns400()
    {
        var response = await factory.CreateClient().GetAsync(new Uri("/api/v1/branches?pageSize=500", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static CreateBranchRequest NewRequest() =>
        new(
            Code: "T" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Name: "Lalitpur Branch",
            Type: "Branch",
            Address: new AddressDto("Bagmati", "Lalitpur", "Lalitpur Metropolitan City", 3, "Pulchowk", null, "44700"),
            PhoneNumber: "01-5523456",
            Email: "lalitpur@afnoparcel.test");
}
