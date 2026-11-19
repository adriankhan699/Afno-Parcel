using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Identity;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;

namespace NepalParcel.IntegrationTests;

public sealed class BookingEndpointsTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private readonly BookingTestHelpers _helpers = new(factory);

    // ---- booking as a customer ---------------------------------------------------------------------------------

    [Fact]
    public async Task Customer_BooksAParcel_AndGetsATrackingNumber_ServerPrice_AndPendingPayment()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();

        var response = await customer.PostAsJsonAsync(ParcelsUri, NewBooking(branch));
        var parcel = await response.Content.ReadFromJsonAsync<ParcelResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(parcel);
        Assert.Equal($"/api/v1/parcels/{parcel.Id}", response.Headers.Location!.AbsolutePath);
        Assert.Matches("^AFN[A-HJKMNP-Z2-9]{11}$", parcel.TrackingNumber);
        Assert.Equal("BookingCreated", parcel.Status);
        Assert.Equal("Online", parcel.Channel);
        Assert.Equal("Standard", parcel.ServiceType);

        // 2.5 kg on the nationwide 1–5 kg band: 150 + 45 × 1.5 = 217.50, plus 13% VAT (28.28).
        Assert.Equal(217.5m, parcel.Price.ShippingCharge);
        Assert.Equal(28.28m, parcel.Price.VatAmount);
        Assert.Equal(245.78m, parcel.Price.TotalCharge);
        Assert.Equal(0m, parcel.CodAmount);
        Assert.Equal("Pending", parcel.PaymentStatus);
        Assert.Equal("Cash", parcel.PaymentMethod);

        Assert.Equal("Sita Gurung", parcel.Receiver.FullName);
        Assert.Equal("Pokhara Metropolitan City", parcel.Receiver.Address.Municipality);
        Assert.Equal("Blue gate", parcel.Receiver.AddressInstructions);
        Assert.Equal("Call before arriving", parcel.Sender.AddressInstructions);
        Assert.Equal(branch, parcel.OriginBranch.Id);
        Assert.Equal(2, parcel.Quantity);
        Assert.True(parcel.CanCancel);
        Assert.Single(parcel.History);

        // Kaski is a hub district: two days for Standard.
        Assert.Equal(parcel.BookedAtUtc.AddDays(2), parcel.EstimatedDeliveryAtUtc);
    }

    [Fact]
    public async Task AnyPriceSentByTheClient_IsIgnored()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        using var body = JsonWith(
            NewBooking(branch),
            ("deliveryCharge", 1),
            ("totalCharge", 1),
            ("shippingCharge", 1),
            ("price", new JsonObject { ["totalCharge"] = 1, ["shippingCharge"] = 1, ["vatAmount"] = 0 }),
            ("codAmountTotal", 0));

        var response = await customer.PostAsync(ParcelsUri, body);
        var parcel = await response.Content.ReadFromJsonAsync<ParcelResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(245.78m, parcel!.Price.TotalCharge);
    }

    [Fact]
    public async Task TheQuote_IsExactlyWhatTheBookingIsChargedAtThatMoment()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();

        var quote = await (await customer.PostAsJsonAsync(QuoteUri, NewQuote(branch, "Express", weight: 3.2m, cod: 2500m)))
            .Content.ReadFromJsonAsync<QuoteResponse>();
        var parcel = await BookAsync(customer, NewBooking(branch, "Express", weight: 3.2m, cod: 2500m));

        Assert.Equal(quote!.Breakdown, parcel.Price);
        Assert.Equal(3.5m, parcel.Price.ChargeableWeightKg);
    }

    [Fact]
    public async Task Booking_IsStoredWithTheAuditTrail_AndTheRuleThatPricedIt()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();

        var parcel = await BookAsync(customer, NewBooking(branch));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var stored = await db.Parcels.AsNoTracking().SingleAsync(p => p.Id == parcel.Id);
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.ParcelBooked && a.EntityId == parcel.Id.ToString());

        Assert.NotNull(stored.PricingRuleId);
        Assert.True(Guid.TryParse(stored.CreatedBy, out _), "CreatedBy should be a user id, not a name or email");
        Assert.NotNull(audit.UserId);
        Assert.DoesNotContain("Sita", audit.NewValues ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(parcel.TrackingNumber, stored.TrackingNumber);
    }

    [Fact]
    public async Task EachBooking_GetsItsOwnTrackingNumber()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();

        var numbers = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            numbers.Add((await BookAsync(customer, NewBooking(branch))).TrackingNumber);
        }

        Assert.Equal(5, numbers.Distinct().Count());
    }

    [Fact]
    public async Task ValleyRoute_UsesTheMoreSpecificCheaperRule()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();

        var parcel = await BookAsync(customer, NewBooking(branch, receiverAddress: LalitpurAddress));

        // Kathmandu → Lalitpur: 100 + 30 × 1.5 = 145.00, VAT 18.85.
        Assert.Equal(145m, parcel.Price.ShippingCharge);
        Assert.Equal(163.85m, parcel.Price.TotalCharge);
    }

    [Fact]
    public async Task ExpressAndSameDay_ArePricedByTheirOwnRules()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();

        var express = await BookAsync(customer, NewBooking(branch, "Express"));
        var sameDay = await BookAsync(customer, NewBooking(branch, "SameDay", receiverAddress: LalitpurAddress));

        Assert.Equal(368.66m, express.Price.TotalCharge); // (225 + 67.50 × 1.5) + 13% VAT
        Assert.Equal(361.6m, sameDay.Price.TotalCharge);  // (300 + 40 × 0.5) + 13% VAT
        Assert.Equal(sameDay.BookedAtUtc.AddHours(10), sameDay.EstimatedDeliveryAtUtc);
        Assert.Equal(express.BookedAtUtc.AddDays(1), express.EstimatedDeliveryAtUtc);
    }

    [Fact]
    public async Task Dimensions_CanMakeTheVolumetricWeightThePricedWeight()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();

        // 60 × 50 × 40 cm ÷ 5000 = 24 kg, so a 2 kg parcel is priced on the 20–100 kg band at 24 kg.
        var parcel = await BookAsync(customer, NewBooking(branch, weight: 2m) with { LengthCm = 60m, WidthCm = 50m, HeightCm = 40m });

        Assert.Equal(24m, parcel.Price.ChargeableWeightKg);
        Assert.Equal(24m, parcel.Price.VolumetricWeightKg);
        Assert.Equal(2m, parcel.WeightKg);
        Assert.Equal(930m + (35m * 4m), parcel.Price.ShippingCharge);
        Assert.Equal(60m, parcel.LengthCm);
    }

    [Fact]
    public async Task PrepaidWithGoodsAmount_AddsACodFee_AndCollectsOnlyTheGoodsMoney()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();

        var parcel = await BookAsync(customer, NewBooking(branch, payment: "Esewa", cod: 4000m));

        Assert.Equal(60m, parcel.Price.CodFee);
        Assert.Equal(313.58m, parcel.Price.TotalCharge);
        Assert.Equal(4000m, parcel.CodAmount);
        Assert.Equal("Pending", parcel.PaymentStatus);
    }

    [Fact]
    public async Task CashOnDelivery_TheReceiverPaysTheChargeAtTheDoor()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();

        var plain = await BookAsync(customer, NewBooking(branch, payment: "CashOnDelivery"));
        var withGoods = await BookAsync(customer, NewBooking(branch, payment: "CashOnDelivery", cod: 4000m));

        Assert.Equal(plain.Price.TotalCharge, plain.CodAmount);
        Assert.Equal(4000m + withGoods.Price.TotalCharge, withGoods.CodAmount);
        Assert.Equal("Pending", withGoods.PaymentStatus);
        Assert.Equal("CashOnDelivery", withGoods.PaymentMethod);
    }

    // ---- booking at the counter --------------------------------------------------------------------------------

    [Fact]
    public async Task CounterStaff_BookForAWalkInCustomer_AndPrepaidCashIsMarkedPaid()
    {
        var staff = await _helpers.StaffAsync();
        var branch = await _helpers.CreateBranchAsync();
        var senderName = NewName("Walkin");

        var parcel = await BookAsync(staff, NewBooking(branch, senderName: senderName));

        Assert.Equal("Counter", parcel.Channel);
        Assert.Equal("Paid", parcel.PaymentStatus);
        Assert.Equal("REF-1", parcel.PaymentReference);
        Assert.True(parcel.CanCancel, "counter staff can cancel a newly created booking");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var walkIn = await db.Customers.AsNoTracking().SingleAsync(c => c.FullName == senderName);
        Assert.True(walkIn.IsWalkIn);
        Assert.Equal("9841234567", walkIn.PhoneNumber);
    }

    [Fact]
    public async Task CounterStaff_ReuseTheSameWalkInCustomerForRepeatBookings()
    {
        var staff = await _helpers.StaffAsync();
        var branch = await _helpers.CreateBranchAsync();
        var senderName = NewName("Repeat");

        var first = await BookAsync(staff, NewBooking(branch, senderName: senderName));
        var second = await BookAsync(staff, NewBooking(branch, senderName: senderName));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var customers = await db.Customers.AsNoTracking().Where(c => c.FullName == senderName).ToListAsync();
        var parcels = await db.Parcels.AsNoTracking().Where(p => p.Id == first.Id || p.Id == second.Id).ToListAsync();
        Assert.Single(customers);
        Assert.All(parcels, p => Assert.Equal(customers[0].Id, p.SenderCustomerId));
    }

    [Fact]
    public async Task CounterStaff_CashOnDelivery_IsNotMarkedPaid()
    {
        var staff = await _helpers.StaffAsync(Roles.BranchManager);
        var branch = await _helpers.CreateBranchAsync();

        var parcel = await BookAsync(staff, NewBooking(branch, payment: "CashOnDelivery"));

        Assert.Equal("Pending", parcel.PaymentStatus);
    }

    // ---- who may book ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Booking_WithoutSigningIn_Returns401()
    {
        var branch = await _helpers.CreateBranchAsync();

        var response = await factory.CreateClient().PostAsJsonAsync(ParcelsUri, NewBooking(branch));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", ProblemMediaType(response));
    }

    [Theory]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.DeliveryAgent)]
    [InlineData(Roles.Accountant)]
    public async Task Booking_ByRolesThatDoNotBook_Returns403(string role)
    {
        var client = await _helpers.StaffAsync(role);
        var branch = await _helpers.CreateBranchAsync();

        var response = await client.PostAsJsonAsync(ParcelsUri, NewBooking(branch));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- business rules and validation -------------------------------------------------------------------------

    [Fact]
    public async Task Booking_ToADistrictWeDoNotServe_Returns422_AndCreatesNothing()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        await SetServiceableAsync("Humla", false);
        try
        {
            var humla = new NepalParcel.Contracts.Common.AddressDto("Karnali", "Humla", "Simkot Rural Municipality", 2, "Bazaar");

            var response = await customer.PostAsJsonAsync(ParcelsUri, NewBooking(branch, receiverAddress: humla, receiverName: NewName("Nowhere")));
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("Business rule violated", problem.GetProperty("title").GetString());
            Assert.Contains("do not deliver to Humla", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            await SetServiceableAsync("Humla", true);
        }
    }

    [Fact]
    public async Task Booking_SameDayToAnotherCity_Returns422_BecauseNoPriceExists()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();

        var response = await customer.PostAsJsonAsync(ParcelsUri, NewBooking(branch, "SameDay"));
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("SameDay delivery is not available", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Booking_WithInvalidFields_Returns400_WithAnErrorPerField()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var request = NewBooking(branch, "Rocket", "Bitcoin", weight: 0m) with
        {
            Quantity = 0,
            LengthCm = 10m,
            Receiver = new BookingPartyDto("", "123", PokharaAddress with { District = "Kathmandu" }),
        };

        var response = await customer.PostAsJsonAsync(ParcelsUri, request);
        var errors = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("errors");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        foreach (var field in new[] { "ServiceType", "PaymentMethod", "WeightKg", "Quantity", "Dimensions", "Receiver.FullName", "Receiver.PhoneNumber", "Receiver.Address.District" })
        {
            Assert.True(errors.TryGetProperty(field, out _), $"expected a validation error for {field}");
        }
    }

    [Fact]
    public async Task Booking_AboveTheWeightLimit_Returns400()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();

        var response = await customer.PostAsJsonAsync(ParcelsUri, NewBooking(branch, weight: 150m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Booking_FromAnUnknownBranch_Returns404()
    {
        var customer = await _helpers.CustomerAsync();

        var response = await customer.PostAsJsonAsync(ParcelsUri, NewBooking(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- quotes ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Quote_WorksWithoutSigningIn_AndExplainsTheCharge()
    {
        var branch = await _helpers.CreateBranchAsync();

        var response = await factory.CreateClient().PostAsJsonAsync(QuoteUri, NewQuote(branch));
        var quote = await response.Content.ReadFromJsonAsync<QuoteResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("NPR", quote!.Currency);
        Assert.Equal("Kaski District", quote.DestinationArea);
        Assert.Equal("Kathmandu District", quote.OriginArea);
        Assert.Equal(2, quote.EstimatedDeliveryDays);
        Assert.Equal(245.78m, quote.Breakdown.TotalCharge);
        Assert.Equal(2.5m, quote.Breakdown.ChargeableWeightKg);
        Assert.False(string.IsNullOrWhiteSpace(quote.RuleName));
    }

    [Fact]
    public async Task Quote_ForAnUnservedRoute_Returns422()
    {
        var branch = await _helpers.CreateBranchAsync();

        var response = await factory.CreateClient().PostAsJsonAsync(QuoteUri, NewQuote(branch, "SameDay"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Quote_InvalidRequest_Returns400()
    {
        var branch = await _helpers.CreateBranchAsync();

        var response = await factory.CreateClient().PostAsJsonAsync(QuoteUri, NewQuote(branch, weight: -1m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- service areas -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ServiceAreas_ListEveryDistrict_WithoutSigningIn()
    {
        var areas = await factory.CreateClient().GetFromJsonAsync<List<ServiceAreaResponse>>(new Uri("/api/v1/service-areas", UriKind.Relative));

        Assert.NotNull(areas);
        Assert.Equal(77, areas.Count);
        Assert.Equal(7, areas.Select(a => a.Province).Distinct().Count());
        Assert.Contains(areas, a => a.District == "Kathmandu" && a.Province == "Bagmati" && a.EstimatedDeliveryDays == 1 && a.IsServiceable);
        Assert.Equal("Koshi", areas[0].Province);
    }

    private async Task SetServiceableAsync(string district, bool serviceable)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var area = await db.ServiceAreas.SingleAsync(a => a.District == district);
        area.SetServiceable(serviceable);
        await db.SaveChangesAsync();
    }
}
