using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Contracts.Warehouses;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;

namespace NepalParcel.IntegrationTests;

/// <summary>Warehouses and the scan API that hub staff use.</summary>
public sealed class ScanAndWarehouseEndpointsTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private static readonly Uri WarehousesUri = new("/api/v1/warehouses", UriKind.Relative);
    private static readonly Uri ScanUri = new("/api/v1/scans", UriKind.Relative);
    private static readonly Uri BatchUri = new("/api/v1/scans/batch", UriKind.Relative);
    private readonly BookingTestHelpers _helpers = new(factory);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>())!;

    private async Task<(ParcelResponse Parcel, Guid Branch, Guid Warehouse)> InTransitParcelAsync()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var warehouse = await _helpers.CreateWarehouseAsync(branch, "Kathmandu Hub");
        var parcel = await BookAsync(customer, NewBooking(branch));
        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.InTransit);
        return (parcel, branch, warehouse);
    }

    // ---- warehouses --------------------------------------------------------------------------------------------

    private static CreateWarehouseRequest NewWarehouse(Guid branchId) =>
        new(branchId, "W" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), "Pokhara Hub", KathmanduAddress, "01-4123456", 2500);

    [Fact]
    public async Task BranchManager_RegistersAWarehouse_AndItIsListed()
    {
        var manager = await _helpers.StaffAsync(Roles.BranchManager);
        var branch = await _helpers.CreateBranchAsync();
        var request = NewWarehouse(branch);

        var response = await manager.PostAsJsonAsync(WarehousesUri, request);
        var created = await ReadAsync<WarehouseResponse>(response);
        var staff = await _helpers.StaffAsync(Roles.WarehouseStaff);
        var list = await ReadAsync<PagedResponse<WarehouseResponse>>(await staff.GetAsync(new Uri("/api/v1/warehouses?pageSize=100", UriKind.Relative)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(request.Code, created.Code);
        Assert.Equal(branch, created.BranchId);
        Assert.Equal("014123456", created.PhoneNumber);
        Assert.True(created.IsActive);
        Assert.Contains(list.Items, w => w.Id == created.Id);
    }

    [Fact]
    public async Task Warehouses_DuplicateCode_Returns409_UnknownBranch404_Invalid400()
    {
        var manager = await _helpers.StaffAsync(Roles.BranchManager);
        var branch = await _helpers.CreateBranchAsync();
        var request = NewWarehouse(branch);
        (await manager.PostAsJsonAsync(WarehousesUri, request)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync(WarehousesUri, request with { Code = request.Code.ToLowerInvariant() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsJsonAsync(WarehousesUri, NewWarehouse(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync(WarehousesUri, NewWarehouse(branch) with { PhoneNumber = "123", Name = "" })).StatusCode);
    }

    [Theory]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.DeliveryAgent)]
    [InlineData(Roles.Customer)]
    public async Task OnlyManagers_CanRegisterWarehouses(string role)
    {
        var client = await _helpers.StaffAsync(role);
        var branch = await _helpers.CreateBranchAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(WarehousesUri, NewWarehouse(branch))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(WarehousesUri, NewWarehouse(branch))).StatusCode);
    }

    [Theory]
    [InlineData(Roles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(Roles.WarehouseStaff, HttpStatusCode.OK)]
    [InlineData(Roles.DeliveryAgent, HttpStatusCode.OK)]
    [InlineData(Roles.Customer, HttpStatusCode.Forbidden)]
    public async Task WarehouseList_IsForStaffOnly(string role, HttpStatusCode expected)
    {
        var client = await _helpers.StaffAsync(role);

        Assert.Equal(expected, (await client.GetAsync(WarehousesUri)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(WarehousesUri)).StatusCode);
    }

    // ---- single scans ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWarehouseScan_RecordsTheArrival_AtThatWarehouse_ByThatPerson()
    {
        var (parcel, _, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAsync(Roles.WarehouseStaff);

        var response = await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber.ToLowerInvariant(), "Arrival", warehouse, "Dock 3"));
        var result = await ReadAsync<ParcelStatusUpdateResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("InTransit", result.PreviousStatus);
        Assert.Equal("ArrivedAtWarehouse", result.Status);
        Assert.Equal("Kathmandu Hub", result.Location);

        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var view = await ReadAsync<ParcelResponse>(await admin.GetAsync(new Uri($"/api/v1/parcels/{parcel.Id}", UriKind.Relative)));
        var last = view.History[^1];
        Assert.Equal("ArrivedAtWarehouse", last.Status);
        Assert.Equal("Kathmandu Hub", last.Location);
        Assert.Equal("Dock 3", last.Remarks);
        Assert.False(string.IsNullOrWhiteSpace(last.ChangedBy));
    }

    [Fact]
    public async Task TheScanTypes_MapToTheirStatuses()
    {
        var (parcel, _, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAsync(Roles.WarehouseStaff);

        var arrival = await ReadAsync<ParcelStatusUpdateResponse>(await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse)));
        var dispatch = await ReadAsync<ParcelStatusUpdateResponse>(await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Dispatch", warehouse)));
        var arrivalAgain = await ReadAsync<ParcelStatusUpdateResponse>(await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "arrival", warehouse)));
        var handedToRider = await ReadAsync<ParcelStatusUpdateResponse>(await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "OutForDelivery", warehouse)));

        Assert.Equal("ArrivedAtWarehouse", arrival.Status);
        Assert.Equal("InTransit", dispatch.Status);
        Assert.Equal("ArrivedAtWarehouse", arrivalAgain.Status);
        Assert.Equal("OutForDelivery", handedToRider.Status);
    }

    [Fact]
    public async Task ScanningTheSameArrivalTwice_Returns422_AndAddsNothing()
    {
        var (parcel, _, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAsync(Roles.WarehouseStaff);
        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse))).EnsureSuccessStatusCode();

        var again = await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var view = await ReadAsync<ParcelResponse>(await admin.GetAsync(new Uri($"/api/v1/parcels/{parcel.Id}", UriKind.Relative)));
        Assert.Equal(1, view.History.Count(h => h.Status == "ArrivedAtWarehouse"));
    }

    [Theory]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.DeliveryAgent)]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.Customer)]
    public async Task OnlyWarehouseStaffAndManagers_CanScan(string role)
    {
        var (parcel, _, warehouse) = await InTransitParcelAsync();
        var client = await _helpers.StaffAsync(role);

        var single = await client.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse));
        var batch = await client.PostAsJsonAsync(BatchUri, new BatchScanRequest("Arrival", warehouse, [parcel.TrackingNumber]));

        Assert.Equal(HttpStatusCode.Forbidden, single.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, batch.StatusCode);
    }

    [Fact]
    public async Task Scanning_WithoutSigningIn_Returns401()
    {
        var (parcel, _, warehouse) = await InTransitParcelAsync();
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(BatchUri, new BatchScanRequest("Arrival", warehouse, [parcel.TrackingNumber]))).StatusCode);
    }

    [Fact]
    public async Task AScan_OfAnUnknownParcel_OrWarehouse_Returns404_AndABadScanTypeReturns400()
    {
        var (parcel, _, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAsync(Roles.WarehouseStaff);

        Assert.Equal(HttpStatusCode.NotFound, (await hub.PostAsJsonAsync(ScanUri, new ScanRequest("AFNAAAAAAAAAAA", "Arrival", warehouse))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Teleport", warehouse))).StatusCode);
    }

    [Fact]
    public async Task APublicTrackingPage_ShowsTheWarehouseName_AfterAScan()
    {
        var (parcel, _, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAsync(Roles.WarehouseStaff);
        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse))).EnsureSuccessStatusCode();

        var tracking = await ReadAsync<PublicTrackingResponse>(
            await factory.CreateClient().GetAsync(new Uri($"/api/v1/tracking/{parcel.TrackingNumber}", UriKind.Relative)));

        Assert.Equal("ArrivedAtWarehouse", tracking.Status);
        Assert.Equal("Kathmandu Hub", tracking.Events[^1].Location);
    }

    // ---- batch scans -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABatch_ReportsEachParcelSeparately_AndOneFailureNeverStopsTheOthers()
    {
        var (first, branch, warehouse) = await InTransitParcelAsync();
        var customer = await _helpers.CustomerAsync();
        var second = await BookAsync(customer, NewBooking(branch));
        await _helpers.AdvanceAsync(second.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped);
        var notShippedYet = await BookAsync(customer, NewBooking(branch));
        var hub = await _helpers.StaffAsync(Roles.WarehouseStaff);

        var response = await hub.PostAsJsonAsync(
            BatchUri,
            new BatchScanRequest("Arrival", warehouse, [first.TrackingNumber, notShippedYet.TrackingNumber, "AFNAAAAAAAAAAA", "garbage", second.TrackingNumber, first.TrackingNumber]));
        var result = await ReadAsync<BatchScanResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, result.Results.Count); // the duplicate is scanned once
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(3, result.Failed);
        Assert.True(result.Results.Single(r => r.TrackingNumber == first.TrackingNumber).Succeeded);
        Assert.Equal("ArrivedAtWarehouse", result.Results.Single(r => r.TrackingNumber == second.TrackingNumber).Status);
        var refused = result.Results.Single(r => r.TrackingNumber == notShippedYet.TrackingNumber);
        Assert.False(refused.Succeeded);
        Assert.Contains("cannot move to", refused.Error, StringComparison.Ordinal);
        Assert.Contains("No parcel", result.Results.Single(r => r.TrackingNumber == "AFNAAAAAAAAAAA").Error, StringComparison.Ordinal);
        Assert.False(result.Results.Single(r => r.TrackingNumber == "GARBAGE").Succeeded);

        // The refused parcel was not touched; the good ones were saved.
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        Assert.Equal("BookingCreated", (await ReadAsync<ParcelResponse>(await admin.GetAsync(new Uri($"/api/v1/parcels/{notShippedYet.Id}", UriKind.Relative)))).Status);
        Assert.Equal("ArrivedAtWarehouse", (await ReadAsync<ParcelResponse>(await admin.GetAsync(new Uri($"/api/v1/parcels/{first.Id}", UriKind.Relative)))).Status);
    }

    [Fact]
    public async Task ABatch_HasLimits_AndAnUnknownWarehouseFailsTheWholeThing()
    {
        var (parcel, _, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAsync(Roles.WarehouseStaff);

        Assert.Equal(HttpStatusCode.BadRequest, (await hub.PostAsJsonAsync(BatchUri, new BatchScanRequest("Arrival", warehouse, []))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await hub.PostAsJsonAsync(BatchUri, new BatchScanRequest("Arrival", warehouse, Enumerable.Repeat(parcel.TrackingNumber, 101).ToList()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await hub.PostAsJsonAsync(BatchUri, new BatchScanRequest("Teleport", warehouse, [parcel.TrackingNumber]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hub.PostAsJsonAsync(BatchUri, new BatchScanRequest("Arrival", Guid.NewGuid(), [parcel.TrackingNumber]))).StatusCode);

        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        Assert.Equal("InTransit", (await ReadAsync<ParcelResponse>(await admin.GetAsync(new Uri($"/api/v1/parcels/{parcel.Id}", UriKind.Relative)))).Status);
    }

    [Fact]
    public async Task ABatchOfOneHundred_IsAccepted()
    {
        var (_, _, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAsync(Roles.WarehouseStaff);
        var numbers = Enumerable.Range(0, 100).Select(_ => TrackingNumbers.Generate()).ToList();

        var response = await hub.PostAsJsonAsync(BatchUri, new BatchScanRequest("Arrival", warehouse, numbers));
        var result = await ReadAsync<BatchScanResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(100, result.Failed);
        Assert.All(result.Results, r => Assert.False(string.IsNullOrWhiteSpace(r.Error)));
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("results").ValueKind);
    }
}
