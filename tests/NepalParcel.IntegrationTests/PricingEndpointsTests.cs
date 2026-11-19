using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Identity;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;

namespace NepalParcel.IntegrationTests;

/// <summary>The pricing rules behind every delivery charge, and the people allowed to change them.</summary>
public sealed class PricingEndpointsTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private static readonly Uri RulesUri = new("/api/v1/pricing/rules", UriKind.Relative);
    private readonly BookingTestHelpers _helpers = new(factory);

    private async Task<ServiceAreaResponse> AreaAsync(string district)
    {
        var areas = await factory.CreateClient().GetFromJsonAsync<List<ServiceAreaResponse>>(new Uri("/api/v1/service-areas", UriKind.Relative));
        return areas!.Single(a => a.District == district);
    }

    [Fact]
    public async Task SuperAdmin_AddsARule_AndNewBookingsUseIt_ThenDeactivatingRestoresTheOldPrice()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var darchula = await AreaAsync("Darchula");

        var before = await BookAsync(customer, NewBooking(branch, receiverAddress: DarchulaAddress));

        var create = await admin.PostAsJsonAsync(
            RulesUri,
            new CreatePricingRuleRequest("Darchula flat rate", "Standard", 0m, 100m, 777m, 0m, 1.5m, Priority: 5, ToServiceAreaId: darchula.Id));
        var rule = await create.Content.ReadFromJsonAsync<PricingRuleResponse>();
        var during = await BookAsync(customer, NewBooking(branch, receiverAddress: DarchulaAddress));

        var deactivate = await admin.PostAsync(new Uri($"/api/v1/pricing/rules/{rule!.Id}/deactivate", UriKind.Relative), null);
        var after = await BookAsync(customer, NewBooking(branch, receiverAddress: DarchulaAddress));

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Equal("Darchula District", rule.ToServiceAreaName);
        Assert.True(rule.IsActive);
        Assert.Equal(245.78m, before.Price.TotalCharge);
        Assert.Equal(878.01m, during.Price.TotalCharge); // 777 flat + 13% VAT (101.01)
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);
        Assert.Equal(before.Price.TotalCharge, after.Price.TotalCharge);
    }

    [Fact]
    public async Task OnlySuperAdmin_CanChangeRules()
    {
        var darchula = await AreaAsync("Darchula");
        var body = new CreatePricingRuleRequest("Nope", "Standard", 0m, 100m, 1m, 0m, 1m, ToServiceAreaId: darchula.Id);

        foreach (var role in new[] { Roles.BranchManager, Roles.Accountant, Roles.BookingStaff, Roles.Customer })
        {
            var client = await _helpers.StaffAsync(role);

            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(RulesUri, body)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(new Uri($"/api/v1/pricing/rules/{Guid.NewGuid()}/deactivate", UriKind.Relative), null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(RulesUri, body)).StatusCode);
    }

    [Theory]
    [InlineData(Roles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(Roles.BranchManager, HttpStatusCode.OK)]
    [InlineData(Roles.Accountant, HttpStatusCode.OK)]
    [InlineData(Roles.BookingStaff, HttpStatusCode.Forbidden)]
    [InlineData(Roles.Customer, HttpStatusCode.Forbidden)]
    public async Task Rules_CanBeListedByManagementRolesOnly(string role, HttpStatusCode expected)
    {
        var client = await _helpers.StaffAsync(role);

        var response = await client.GetAsync(new Uri("/api/v1/pricing/rules?pageSize=10", UriKind.Relative));

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var page = await response.Content.ReadFromJsonAsync<PagedResponse<PricingRuleResponse>>();
            Assert.Equal(10, page!.Items.Count);
            Assert.True(page.TotalCount > 10, "the development price list is seeded");
            Assert.All(page.Items, r => Assert.True(r.MinWeightKg < r.MaxWeightKg));
        }
    }

    [Fact]
    public async Task CreateRule_Validates()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var request = new CreatePricingRuleRequest("", "Rocket", 5m, 1m, -1m, -1m, 150m);

        var response = await admin.PostAsJsonAsync(RulesUri, request);
        var errors = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("errors");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        foreach (var field in new[] { "Name", "ServiceType", "MaxWeightKg", "BasePrice", "PricePerKg", "CodFeePercent" })
        {
            Assert.True(errors.TryGetProperty(field, out _), $"expected a validation error for {field}");
        }
    }

    [Fact]
    public async Task CreateRule_ForAnUnknownServiceArea_Returns404()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);

        var response = await admin.PostAsJsonAsync(
            RulesUri,
            new CreatePricingRuleRequest("Ghost route", "Standard", 0m, 5m, 100m, 10m, 1m, FromServiceAreaId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeactivateRule_TwiceReturns409_AndUnknownReturns404()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var area = await AreaAsync("Mugu");
        var created = await (await admin.PostAsJsonAsync(
            RulesUri,
            new CreatePricingRuleRequest("Mugu temp", "Standard", 0m, 5m, 100m, 10m, 1m, ToServiceAreaId: area.Id)))
            .Content.ReadFromJsonAsync<PricingRuleResponse>();
        var uri = new Uri($"/api/v1/pricing/rules/{created!.Id}/deactivate", UriKind.Relative);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync(uri, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(uri, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync(new Uri($"/api/v1/pricing/rules/{Guid.NewGuid()}/deactivate", UriKind.Relative), null)).StatusCode);
    }

    [Fact]
    public async Task ARuleThatStartsInTheFuture_DoesNotAffectPricesYet()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var area = await AreaAsync("Bajura");
        var normal = await BookAsync(customer, NewBooking(branch, receiverAddress: new AddressDto("Sudurpashchim", "Bajura", "Martadi Municipality", 1, "Bazaar")));

        (await admin.PostAsJsonAsync(
            RulesUri,
            new CreatePricingRuleRequest("Bajura next year", "Standard", 0m, 100m, 1m, 0m, 1m, Priority: 9, ToServiceAreaId: area.Id, EffectiveFromUtc: DateTimeOffset.UtcNow.AddYears(1))))
            .EnsureSuccessStatusCode();
        var still = await BookAsync(customer, NewBooking(branch, receiverAddress: new AddressDto("Sudurpashchim", "Bajura", "Martadi Municipality", 1, "Bazaar")));

        Assert.Equal(normal.Price.TotalCharge, still.Price.TotalCharge);
    }
}
