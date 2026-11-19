using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;

namespace NepalParcel.IntegrationTests;

/// <summary>Reading, searching, cancelling, paying for and printing parcels.</summary>
public sealed class ParcelEndpointsTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private readonly BookingTestHelpers _helpers = new(factory);

    private static Uri ParcelUri(Guid id, string suffix = "") => new($"/api/v1/parcels/{id}{suffix}", UriKind.Relative);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>())!;

    // ---- details and access control ----------------------------------------------------------------------------

    [Fact]
    public async Task Owner_CanReadTheirParcel_ById_AndByTrackingNumber()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var booked = await BookAsync(customer, NewBooking(branch));

        var byId = await ReadAsync<ParcelResponse>(await customer.GetAsync(ParcelUri(booked.Id)));
        var byTracking = await ReadAsync<ParcelResponse>(
            await customer.GetAsync(new Uri($"/api/v1/parcels/tracking/{booked.TrackingNumber.ToLowerInvariant()}", UriKind.Relative)));

        Assert.Equal(booked.TrackingNumber, byId.TrackingNumber);
        Assert.Equal(booked.Id, byTracking.Id);
        Assert.Equal("BookingCreated", byId.History[0].Status);
        Assert.Equal("Parcel booked", byId.History[0].Remarks);
    }

    [Fact]
    public async Task AnotherCustomer_CannotSeeOrCancelAParcel_ItLooksLikeItDoesNotExist()
    {
        var owner = await _helpers.CustomerAsync();
        var stranger = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var booked = await BookAsync(owner, NewBooking(branch));

        var byId = await stranger.GetAsync(ParcelUri(booked.Id));
        var byTracking = await stranger.GetAsync(new Uri($"/api/v1/parcels/tracking/{booked.TrackingNumber}", UriKind.Relative));
        var receipt = await stranger.GetAsync(ParcelUri(booked.Id, "/receipt"));
        var cancel = await stranger.PostAsJsonAsync(ParcelUri(booked.Id, "/cancel"), new CancelParcelRequest("Trying to cancel it"));

        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byTracking.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, receipt.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        var stillBooked = await ReadAsync<ParcelResponse>(await owner.GetAsync(ParcelUri(booked.Id)));
        Assert.Equal("BookingCreated", stillBooked.Status);
    }

    [Theory]
    [InlineData(Roles.SuperAdmin)]
    [InlineData(Roles.BranchManager)]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.Accountant)]
    public async Task StaffRoles_CanReadAnyParcel(string role)
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var booked = await BookAsync(customer, NewBooking(branch));
        var staff = await _helpers.StaffAsync(role);

        var response = await staff.GetAsync(ParcelUri(booked.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeliveryAgents_CannotListOrReadParcels_YetAnonymousCallersGet401()
    {
        var agent = await _helpers.StaffAsync(Roles.DeliveryAgent);

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync(ParcelsUri)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(ParcelsUri)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(ParcelUri(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task Unknown_OrMalformedTrackingNumbers_Return404()
    {
        var staff = await _helpers.StaffAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync(ParcelUri(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync(new Uri("/api/v1/parcels/tracking/AFNAAAAAAAAAAA", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync(new Uri("/api/v1/parcels/tracking/nonsense", UriKind.Relative))).StatusCode);
    }

    // ---- searching, filtering and paging -----------------------------------------------------------------------

    [Fact]
    public async Task Customer_ListsOnlyTheirOwnParcels_NewestFirst()
    {
        var alice = await _helpers.CustomerAsync();
        var bob = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var first = await BookAsync(alice, NewBooking(branch));
        await Task.Delay(20);
        var second = await BookAsync(alice, NewBooking(branch));
        var bobs = await BookAsync(bob, NewBooking(branch));

        var alicesList = await ReadAsync<PagedResponse<ParcelSummaryResponse>>(await alice.GetAsync(ParcelsUri));
        var bobsList = await ReadAsync<PagedResponse<ParcelSummaryResponse>>(await bob.GetAsync(ParcelsUri));

        Assert.Equal([second.Id, first.Id], alicesList.Items.Select(i => i.Id).ToArray());
        Assert.Equal(2, alicesList.TotalCount);
        Assert.Equal([bobs.Id], bobsList.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Staff_SeeParcelsOfEveryCustomer()
    {
        var alice = await _helpers.CustomerAsync();
        var bob = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var marker = NewName("Staffview");
        await BookAsync(alice, NewBooking(branch, receiverName: marker));
        await BookAsync(bob, NewBooking(branch, receiverName: marker));
        var staff = await _helpers.StaffAsync();

        var list = await ReadAsync<PagedResponse<ParcelSummaryResponse>>(await staff.GetAsync(new Uri($"/api/v1/parcels?search={marker}", UriKind.Relative)));

        Assert.Equal(2, list.TotalCount);
    }

    [Fact]
    public async Task List_Paginates()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        for (var i = 0; i < 5; i++)
        {
            await BookAsync(customer, NewBooking(branch));
        }

        var page1 = await ReadAsync<PagedResponse<ParcelSummaryResponse>>(await customer.GetAsync(new Uri("/api/v1/parcels?page=1&pageSize=2", UriKind.Relative)));
        var page3 = await ReadAsync<PagedResponse<ParcelSummaryResponse>>(await customer.GetAsync(new Uri("/api/v1/parcels?page=3&pageSize=2", UriKind.Relative)));
        var page4 = await ReadAsync<PagedResponse<ParcelSummaryResponse>>(await customer.GetAsync(new Uri("/api/v1/parcels?page=4&pageSize=2", UriKind.Relative)));

        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(3, page1.TotalPages);
        Assert.True(page1.HasNextPage);
        Assert.Single(page3.Items);
        Assert.False(page3.HasNextPage);
        Assert.Empty(page4.Items);
        Assert.Empty(page1.Items.Select(i => i.Id).Intersect(page3.Items.Select(i => i.Id)));
    }

    [Fact]
    public async Task Search_FindsByTrackingNumber_ReceiverName_AndPhone()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var receiver = NewName("Receiver");
        var phone = NewPhone();
        var target = await BookAsync(customer, NewBooking(branch, receiverName: receiver, receiverPhone: phone));
        await BookAsync(customer, NewBooking(branch));

        var byTracking = await SearchAsync(customer, target.TrackingNumber);
        var byTrackingPrefix = await SearchAsync(customer, target.TrackingNumber[..8].ToLowerInvariant());
        var byName = await SearchAsync(customer, receiver.ToUpperInvariant());
        var byNamePart = await SearchAsync(customer, receiver[^6..]);
        var byPhone = await SearchAsync(customer, phone);
        var byPhoneTail = await SearchAsync(customer, phone[^6..]);
        var nothing = await SearchAsync(customer, "zzz-no-such-thing");

        Assert.Equal([target.Id], byTracking.Items.Select(i => i.Id).ToArray());
        Assert.Contains(target.Id, byTrackingPrefix.Items.Select(i => i.Id));
        Assert.Equal([target.Id], byName.Items.Select(i => i.Id).ToArray());
        Assert.Equal([target.Id], byNamePart.Items.Select(i => i.Id).ToArray());
        Assert.Equal([target.Id], byPhone.Items.Select(i => i.Id).ToArray());
        Assert.Contains(target.Id, byPhoneTail.Items.Select(i => i.Id));
        Assert.Empty(nothing.Items);
    }

    [Fact]
    public async Task Search_TreatsPercentAndUnderscoreAsPlainCharacters()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        await BookAsync(customer, NewBooking(branch, receiverName: NewName("Plain")));

        Assert.Empty((await SearchAsync(customer, "%%%")).Items);
        Assert.Empty((await SearchAsync(customer, "___")).Items);
    }

    [Fact]
    public async Task Filters_NarrowTheList()
    {
        var customer = await _helpers.CustomerAsync();
        var staff = await _helpers.StaffAsync();
        var branch = await _helpers.CreateBranchAsync();
        var otherBranch = await _helpers.CreateBranchAsync();
        var marker = NewName("Filter");

        var standardCash = await BookAsync(customer, NewBooking(branch, receiverName: marker));
        var expressEsewaCod = await BookAsync(customer, NewBooking(branch, "Express", "Esewa", cod: 1000m, receiverName: marker));
        var counterCod = await BookAsync(staff, NewBooking(otherBranch, "Standard", "CashOnDelivery", receiverName: marker));
        await _helpers.AdvanceAsync(standardCash.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.InTransit);

        async Task<Guid[]> Ids(string query)
        {
            var page = await ReadAsync<PagedResponse<ParcelSummaryResponse>>(
                await staff.GetAsync(new Uri($"/api/v1/parcels?search={marker}&{query}", UriKind.Relative)));
            return page.Items.Select(i => i.Id).Order().ToArray();
        }

        Assert.Equal(3, (await Ids(string.Empty)).Length);
        Assert.Equal([standardCash.Id], await Ids("status=InTransit"));
        Assert.Equal(new[] { expressEsewaCod.Id, counterCod.Id }.Order().ToArray(), await Ids("status=BookingCreated"));
        Assert.Equal([expressEsewaCod.Id], await Ids("serviceType=Express"));
        Assert.Equal([expressEsewaCod.Id], await Ids("paymentMethod=esewa"));
        Assert.Equal(new[] { expressEsewaCod.Id, counterCod.Id }.Order().ToArray(), await Ids("isCod=true"));
        Assert.Equal([standardCash.Id], await Ids("isCod=false"));
        Assert.Equal([counterCod.Id], await Ids("channel=Counter"));
        Assert.Equal(new[] { standardCash.Id, expressEsewaCod.Id }.Order().ToArray(), await Ids("channel=Online"));
        Assert.Equal([counterCod.Id], await Ids($"originBranchId={otherBranch}"));
        Assert.Empty(await Ids("status=Delivered"));
    }

    [Fact]
    public async Task DateFilter_UsesNepalCalendarDays()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var marker = NewName("Dates");
        var booked = await BookAsync(customer, NewBooking(branch, receiverName: marker));
        var nepalToday = DateOnly.FromDateTime(booked.BookedAtUtc.ToOffset(TimeSpan.FromMinutes(345)).DateTime);

        async Task<int> Count(string query) =>
            (await ReadAsync<PagedResponse<ParcelSummaryResponse>>(
                await customer.GetAsync(new Uri($"/api/v1/parcels?search={marker}&{query}", UriKind.Relative)))).TotalCount;

        Assert.Equal(1, await Count($"bookedFrom={nepalToday:yyyy-MM-dd}&bookedTo={nepalToday:yyyy-MM-dd}"));
        Assert.Equal(1, await Count($"bookedFrom={nepalToday.AddDays(-3):yyyy-MM-dd}"));
        Assert.Equal(0, await Count($"bookedTo={nepalToday.AddDays(-1):yyyy-MM-dd}"));
        Assert.Equal(0, await Count($"bookedFrom={nepalToday.AddDays(1):yyyy-MM-dd}"));
    }

    [Theory]
    [InlineData("status=Teleported")]
    [InlineData("serviceType=Rocket")]
    [InlineData("paymentMethod=Bitcoin")]
    [InlineData("channel=Phone")]
    [InlineData("pageSize=500")]
    [InlineData("page=0")]
    [InlineData("bookedFrom=2026-11-10&bookedTo=2026-11-01")]
    public async Task List_InvalidFilters_Return400(string query)
    {
        var customer = await _helpers.CustomerAsync();

        var response = await customer.GetAsync(new Uri($"/api/v1/parcels?{query}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListRows_CarryWhatTheTableNeeds()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var booked = await BookAsync(customer, NewBooking(branch, receiverName: NewName("Row")));

        var row = (await SearchAsync(customer, booked.TrackingNumber)).Items.Single();

        Assert.Equal("BookingCreated", row.Status);
        Assert.Equal("Booking created", row.StatusName);
        Assert.Equal("Standard", row.ServiceType);
        Assert.Equal("Online", row.Channel);
        Assert.Equal("Cash", row.PaymentMethod);
        Assert.Equal("Pending", row.PaymentStatus);
        Assert.Equal("Kaski", row.ReceiverDistrict);
        Assert.Equal("Pokhara Metropolitan City", row.ReceiverMunicipality);
        Assert.Equal("Test Branch", row.OriginBranchName);
        Assert.Equal(245.78m, row.DeliveryCharge);
        Assert.Equal(2, row.Quantity);
    }

    // ---- cancelling --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Customer_CancelsABooking_BeforePickup_AndThePendingPaymentIsVoided()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var booked = await BookAsync(customer, NewBooking(branch));

        var response = await customer.PostAsJsonAsync(ParcelUri(booked.Id, "/cancel"), new CancelParcelRequest("Changed my mind"));
        var cancelled = await ReadAsync<ParcelResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.False(cancelled.CanCancel);
        Assert.Equal("Failed", cancelled.PaymentStatus);
        Assert.Equal(["BookingCreated", "Cancelled"], cancelled.History.Select(h => h.Status).ToArray());
        Assert.Contains("Changed my mind", cancelled.History[1].Remarks, StringComparison.Ordinal);

        var listed = (await SearchAsync(customer, booked.TrackingNumber)).Items.Single();
        Assert.Equal("Cancelled", listed.Status);
    }

    [Fact]
    public async Task Customer_CannotCancel_OncePickedUp_ButCounterStaffCan_AndPaidMoneyIsFlaggedRefunded()
    {
        var customer = await _helpers.CustomerAsync();
        var staff = await _helpers.StaffAsync();
        var branch = await _helpers.CreateBranchAsync();
        var booked = await BookAsync(staff, NewBooking(branch)); // counter booking: cash taken at the desk
        var online = await BookAsync(customer, NewBooking(branch));
        await _helpers.AdvanceAsync(online.Id, ParcelStatusCode.PickedUp);
        await _helpers.AdvanceAsync(booked.Id, ParcelStatusCode.PickedUp);

        var customerAttempt = await customer.PostAsJsonAsync(ParcelUri(online.Id, "/cancel"), new CancelParcelRequest("Changed my mind"));
        var problem = JsonDocument.Parse(await customerAttempt.Content.ReadAsStringAsync()).RootElement;
        var seenByCustomer = await ReadAsync<ParcelResponse>(await customer.GetAsync(ParcelUri(online.Id)));
        var staffCancel = await ReadAsync<ParcelResponse>(
            await staff.PostAsJsonAsync(ParcelUri(booked.Id, "/cancel"), new CancelParcelRequest("Customer asked at the counter")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, customerAttempt.StatusCode);
        Assert.Contains("before it is picked up", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(seenByCustomer.CanCancel);
        Assert.Equal("PickedUp", seenByCustomer.Status);
        Assert.Equal("Cancelled", staffCancel.Status);
        Assert.Equal("Refunded", staffCancel.PaymentStatus);
    }

    [Fact]
    public async Task Staff_CannotCancel_OnceTheParcelIsInTransit()
    {
        var staff = await _helpers.StaffAsync();
        var branch = await _helpers.CreateBranchAsync();
        var booked = await BookAsync(staff, NewBooking(branch));
        await _helpers.AdvanceAsync(booked.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.InTransit);

        var response = await staff.PostAsJsonAsync(ParcelUri(booked.Id, "/cancel"), new CancelParcelRequest("Far too late"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Cancelling_NeedsAReason_AndWorksOnlyOnce()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var booked = await BookAsync(customer, NewBooking(branch));

        var noReason = await customer.PostAsJsonAsync(ParcelUri(booked.Id, "/cancel"), new CancelParcelRequest("no"));
        var first = await customer.PostAsJsonAsync(ParcelUri(booked.Id, "/cancel"), new CancelParcelRequest("Changed my mind"));
        var second = await customer.PostAsJsonAsync(ParcelUri(booked.Id, "/cancel"), new CancelParcelRequest("Changed my mind again"));
        var problem = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Contains("already cancelled", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_Anonymously_Returns401_AndWarehouseStaffGet403()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var booked = await BookAsync(customer, NewBooking(branch));
        var warehouse = await _helpers.StaffAsync(Roles.WarehouseStaff);
        var body = new CancelParcelRequest("Trying to cancel it");

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(ParcelUri(booked.Id, "/cancel"), body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await warehouse.PostAsJsonAsync(ParcelUri(booked.Id, "/cancel"), body)).StatusCode);
    }

    // ---- confirming payment ------------------------------------------------------------------------------------

    [Fact]
    public async Task CounterStaff_ConfirmAPendingPrepaidPayment_OnlyOnce()
    {
        var customer = await _helpers.CustomerAsync();
        var staff = await _helpers.StaffAsync();
        var branch = await _helpers.CreateBranchAsync();
        var booked = await BookAsync(customer, NewBooking(branch));

        var first = await staff.PostAsJsonAsync(ParcelUri(booked.Id, "/confirm-payment"), new ConfirmPaymentRequest("RECEIPT-42"));
        var paid = await ReadAsync<ParcelResponse>(first);
        var second = await staff.PostAsJsonAsync(ParcelUri(booked.Id, "/confirm-payment"), new ConfirmPaymentRequest());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("Paid", paid.PaymentStatus);
        Assert.Equal("RECEIPT-42", paid.PaymentReference);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.ParcelId == booked.Id);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal(245.78m, payment.Amount);
        Assert.NotNull(payment.PaidAtUtc);
    }

    [Fact]
    public async Task ConfirmPayment_IsForCounterStaffOnly()
    {
        var customer = await _helpers.CustomerAsync();
        var accountant = await _helpers.StaffAsync(Roles.Accountant);
        var branch = await _helpers.CreateBranchAsync();
        var booked = await BookAsync(customer, NewBooking(branch));

        var byCustomer = await customer.PostAsJsonAsync(ParcelUri(booked.Id, "/confirm-payment"), new ConfirmPaymentRequest());
        var byAccountant = await accountant.PostAsJsonAsync(ParcelUri(booked.Id, "/confirm-payment"), new ConfirmPaymentRequest());
        var anonymous = await factory.CreateClient().PostAsJsonAsync(ParcelUri(booked.Id, "/confirm-payment"), new ConfirmPaymentRequest());

        Assert.Equal(HttpStatusCode.Forbidden, byCustomer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byAccountant.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task ConfirmPayment_NotForCashOnDelivery_NotForCancelledBookings()
    {
        var customer = await _helpers.CustomerAsync();
        var staff = await _helpers.StaffAsync();
        var branch = await _helpers.CreateBranchAsync();
        var cod = await BookAsync(customer, NewBooking(branch, payment: "CashOnDelivery"));
        var cancelled = await BookAsync(customer, NewBooking(branch));
        (await customer.PostAsJsonAsync(ParcelUri(cancelled.Id, "/cancel"), new CancelParcelRequest("Changed my mind"))).EnsureSuccessStatusCode();

        var forCod = await staff.PostAsJsonAsync(ParcelUri(cod.Id, "/confirm-payment"), new ConfirmPaymentRequest());
        var forCancelled = await staff.PostAsJsonAsync(ParcelUri(cancelled.Id, "/confirm-payment"), new ConfirmPaymentRequest());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, forCod.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, forCancelled.StatusCode);
    }

    // ---- receipts ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Receipt_HasEverythingNeededToPrintIt()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var booked = await BookAsync(customer, NewBooking(branch, payment: "Esewa", cod: 4000m));

        var response = await customer.GetAsync(ParcelUri(booked.Id, "/receipt"));
        var receipt = await ReadAsync<ReceiptResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"R-{booked.TrackingNumber}", receipt.ReceiptNumber);
        Assert.Equal("Afno Parcel Test Co", receipt.CompanyName);
        Assert.Equal("01-4000000", receipt.CompanyPhone);
        Assert.Equal(booked.TrackingNumber, receipt.Parcel.TrackingNumber);
        Assert.Equal("Ram Sharma", receipt.Parcel.Sender.FullName);
        Assert.Equal("Sita Gurung", receipt.Parcel.Receiver.FullName);
        Assert.Equal("Books and clothes", receipt.Parcel.Description);
        Assert.Equal(313.58m, receipt.Parcel.Price.TotalCharge);
        Assert.Equal(60m, receipt.Parcel.Price.CodFee);
        Assert.Equal(4000m, receipt.Parcel.CodAmount);
        Assert.Equal("Esewa", receipt.Parcel.PaymentMethod);
        Assert.NotNull(receipt.Parcel.EstimatedDeliveryAtUtc);
        Assert.Equal("Test Branch", receipt.Parcel.OriginBranch.Name);
    }

    [Fact]
    public async Task Receipt_ForUnknownParcel_Returns404()
    {
        var staff = await _helpers.StaffAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync(ParcelUri(Guid.NewGuid(), "/receipt"))).StatusCode);
    }

    [Fact]
    public async Task APriceStaysTheSame_EvenAfterTheRulesChange()
    {
        // A rule just for Jajarkot, so nothing else in this test class is affected when it is switched off again.
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var areas = await factory.CreateClient().GetFromJsonAsync<List<ServiceAreaResponse>>(new Uri("/api/v1/service-areas", UriKind.Relative));
        var jajarkot = areas!.Single(a => a.District == "Jajarkot");
        var rule = await ReadAsync<PricingRuleResponse>(await admin.PostAsJsonAsync(
            new Uri("/api/v1/pricing/rules", UriKind.Relative),
            new CreatePricingRuleRequest("Jajarkot flat", "Standard", 0m, 100m, 500m, 0m, 1.5m, Priority: 5, ToServiceAreaId: jajarkot.Id)));
        var address = new AddressDto("Karnali", "Jajarkot", "Bheri Municipality", 2, "Bazaar");
        var booked = await BookAsync(customer, NewBooking(branch, receiverAddress: address));

        (await admin.PostAsync(new Uri($"/api/v1/pricing/rules/{rule.Id}/deactivate", UriKind.Relative), null)).EnsureSuccessStatusCode();
        var rebooked = await BookAsync(customer, NewBooking(branch, receiverAddress: address));
        var later = await ReadAsync<ParcelResponse>(await customer.GetAsync(ParcelUri(booked.Id)));
        var receipt = await ReadAsync<ReceiptResponse>(await customer.GetAsync(ParcelUri(booked.Id, "/receipt")));

        Assert.Equal(565m, booked.Price.TotalCharge);        // 500 flat + 13% VAT
        Assert.Equal(245.78m, rebooked.Price.TotalCharge);   // the flat rule is off, so the nationwide price applies again
        Assert.Equal(booked.Price, later.Price);
        Assert.Equal(booked.Price, receipt.Parcel.Price);
    }

    private static async Task<PagedResponse<ParcelSummaryResponse>> SearchAsync(HttpClient client, string term)
    {
        var response = await client.GetAsync(new Uri($"/api/v1/parcels?search={Uri.EscapeDataString(term)}&pageSize=100", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResponse<ParcelSummaryResponse>>(response);
    }
}
