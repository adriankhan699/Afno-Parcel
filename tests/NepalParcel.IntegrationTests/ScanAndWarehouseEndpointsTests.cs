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

    private static Uri ParcelUri(Guid id) => new($"/api/v1/parcels/{id}", UriKind.Relative);

    private static Uri MovementsUri(Guid id) => new($"/api/v1/parcels/{id}/movements", UriKind.Relative);

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
    public async Task SuperAdmin_RegistersAWarehouse_AndItIsListedWithItsBranchName()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var branch = await _helpers.CreateBranchAsync();
        var request = NewWarehouse(branch);

        var response = await admin.PostAsJsonAsync(WarehousesUri, request);
        var created = await ReadAsync<WarehouseResponse>(response);
        var staff = await _helpers.StaffAsync(Roles.WarehouseStaff);
        var list = await ReadAsync<PagedResponse<WarehouseResponse>>(await staff.GetAsync(new Uri("/api/v1/warehouses?pageSize=100", UriKind.Relative)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(request.Code, created.Code);
        Assert.Equal(branch, created.BranchId);
        Assert.Equal("014123456", created.PhoneNumber);
        Assert.True(created.IsActive);
        var listed = Assert.Single(list.Items, w => w.Id == created.Id);
        Assert.Equal("Test Branch", listed.BranchName);
        Assert.Equal(0, listed.ParcelsHeld);
    }

    [Fact]
    public async Task ABranchManager_CanAddWarehousesToTheirOwnBranchOnly()
    {
        var own = await _helpers.CreateBranchAsync();
        var other = await _helpers.CreateBranchAsync();
        var manager = await _helpers.StaffAtAsync(Roles.BranchManager, own);

        Assert.Equal(HttpStatusCode.Created, (await manager.PostAsJsonAsync(WarehousesUri, NewWarehouse(own))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(WarehousesUri, NewWarehouse(other))).StatusCode);
    }

    [Fact]
    public async Task Warehouses_DuplicateCode_Returns409_UnknownBranch404_Invalid400()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var branch = await _helpers.CreateBranchAsync();
        var request = NewWarehouse(branch);
        (await admin.PostAsJsonAsync(WarehousesUri, request)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(WarehousesUri, request with { Code = request.Code.ToLowerInvariant() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(WarehousesUri, NewWarehouse(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(WarehousesUri, NewWarehouse(branch) with { PhoneNumber = "123", Name = "" })).StatusCode);
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

    [Fact]
    public async Task AWarehouse_CanBeEdited_ButNotItsCodeOrBranch()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var branch = await _helpers.CreateBranchAsync();
        var created = await ReadAsync<WarehouseResponse>(await admin.PostAsJsonAsync(WarehousesUri, NewWarehouse(branch)));
        var uri = new Uri($"/api/v1/warehouses/{created.Id}", UriKind.Relative);

        var response = await admin.PutAsJsonAsync(uri, new UpdateWarehouseRequest("Renamed Hub", PokharaAddress, "9841999999", 900));
        var updated = await ReadAsync<WarehouseResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed Hub", updated.Name);
        Assert.Equal(created.Code, updated.Code);
        Assert.Equal(branch, updated.BranchId);
        Assert.Equal(900, updated.CapacityParcels);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(uri, new UpdateWarehouseRequest("", PokharaAddress, "9841999999"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync(new Uri($"/api/v1/warehouses/{Guid.NewGuid()}", UriKind.Relative), new UpdateWarehouseRequest("X Hub", PokharaAddress, "9841999999"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _helpers.StaffAsync(Roles.WarehouseStaff)).PutAsJsonAsync(uri, new UpdateWarehouseRequest("Hack", PokharaAddress, "9841999999"))).StatusCode);
    }

    [Fact]
    public async Task AWarehouse_CanBeSwitchedOffAndOn_ButNotWhileItHoldsParcels()
    {
        var (parcel, branch, warehouse) = await InTransitParcelAsync();
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);
        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse))).EnsureSuccessStatusCode();
        var off = new Uri($"/api/v1/warehouses/{warehouse}/deactivate", UriKind.Relative);
        var on = new Uri($"/api/v1/warehouses/{warehouse}/activate", UriKind.Relative);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync(off, null)).StatusCode); // it holds a parcel

        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "OutForDelivery", warehouse))).EnsureSuccessStatusCode();
        var deactivated = await admin.PostAsync(off, null);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.False((await ReadAsync<WarehouseResponse>(deactivated)).IsActive);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(off, null)).StatusCode);

        var staff = await _helpers.StaffAsync(Roles.WarehouseStaff);
        var visible = await ReadAsync<PagedResponse<WarehouseResponse>>(await staff.GetAsync(new Uri("/api/v1/warehouses?pageSize=100", UriKind.Relative)));
        Assert.DoesNotContain(visible.Items, w => w.Id == warehouse);
        var withInactive = await ReadAsync<PagedResponse<WarehouseResponse>>(await admin.GetAsync(new Uri("/api/v1/warehouses?pageSize=100&includeInactive=true", UriKind.Relative)));
        Assert.Contains(withInactive.Items, w => w.Id == warehouse && !w.IsActive);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(on, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(on, null)).StatusCode);
    }

    // ---- single scans ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWarehouseScan_RecordsTheArrival_AtThatWarehouse_ByThatPerson()
    {
        var (parcel, branch, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);

        var response = await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber.ToLowerInvariant(), "Arrival", warehouse, "Dock 3"));
        var result = await ReadAsync<ScanResultDto>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Applied", result.Outcome);
        Assert.Equal("ArrivedAtWarehouse", result.Status);
        Assert.Equal("Kathmandu Hub", result.Warehouse);
        Assert.Equal(parcel.TrackingNumber, result.TrackingNumber);

        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var view = await ReadAsync<ParcelResponse>(await admin.GetAsync(ParcelUri(parcel.Id)));
        var last = view.History[^1];
        Assert.Equal("ArrivedAtWarehouse", last.Status);
        Assert.Equal("Kathmandu Hub", last.Location);
        Assert.Equal("Dock 3", last.Remarks);
        Assert.False(string.IsNullOrWhiteSpace(last.ChangedBy));

        var movements = await ReadAsync<List<ParcelMovementResponse>>(await admin.GetAsync(MovementsUri(parcel.Id)));
        var movement = Assert.Single(movements);
        Assert.Equal("Arrival", movement.ScanType);
        Assert.Equal(warehouse, movement.WarehouseId);
        Assert.Equal("Kathmandu Hub", movement.WarehouseName);
        Assert.Equal("Dock 3", movement.Remarks);
        Assert.False(movement.IsUnexpectedArrival);
        Assert.False(string.IsNullOrWhiteSpace(movement.StaffName));
    }

    [Fact]
    public async Task EveryScanIsLogged_WithParcelWarehouseStaffTypeTimeAndRemarks()
    {
        var (parcel, branch, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse, "Dock 3"))).EnsureSuccessStatusCode();

        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var log = await ReadAsync<PagedResponse<ParcelScanResponse>>(
            await admin.GetAsync(new Uri($"/api/v1/scans?trackingNumber={parcel.TrackingNumber}", UriKind.Relative)));

        var row = Assert.Single(log.Items);
        Assert.Equal(parcel.Id, row.ParcelId);
        Assert.Equal(parcel.TrackingNumber, row.TrackingNumber);
        Assert.Equal(warehouse, row.WarehouseId);
        Assert.Equal("Kathmandu Hub", row.WarehouseName);
        Assert.NotEqual(Guid.Empty, row.StaffUserId);
        Assert.False(string.IsNullOrWhiteSpace(row.StaffName));
        Assert.Equal("Arrival", row.ScanType);
        Assert.Equal("Applied", row.Outcome);
        Assert.Equal("Dock 3", row.Remarks);
        Assert.True(row.ScannedAtUtc >= before && row.ScannedAtUtc <= DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task ScanningTheSameArrivalTwice_IsAcceptedAsADuplicate_AndCreatesNoSecondMovement()
    {
        var (parcel, branch, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);
        var first = await ReadAsync<ScanResultDto>(await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse)));

        var againResponse = await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse, "Scanned twice"));
        var again = await ReadAsync<ScanResultDto>(againResponse);

        Assert.Equal("Applied", first.Outcome);
        Assert.Equal(HttpStatusCode.OK, againResponse.StatusCode);
        Assert.Equal("Duplicate", again.Outcome);
        Assert.True(again.Succeeded);
        Assert.Contains("Already recorded", again.Message, StringComparison.Ordinal);

        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var view = await ReadAsync<ParcelResponse>(await admin.GetAsync(ParcelUri(parcel.Id)));
        Assert.Equal(1, view.History.Count(h => h.Status == "ArrivedAtWarehouse"));
        Assert.Single(await ReadAsync<List<ParcelMovementResponse>>(await admin.GetAsync(MovementsUri(parcel.Id))));

        // The repeat is still written to the scan log, so nothing a person did is lost.
        var log = await ReadAsync<PagedResponse<ParcelScanResponse>>(
            await admin.GetAsync(new Uri($"/api/v1/scans?trackingNumber={parcel.TrackingNumber}", UriKind.Relative)));
        Assert.Equal(2, log.TotalCount);
        Assert.Contains(log.Items, s => s.Outcome == "Duplicate" && s.Remarks == "Scanned twice");
        Assert.Single(log.Items.Select(s => s.MovementId).Distinct());

        var duplicatesOnly = await ReadAsync<PagedResponse<ParcelScanResponse>>(
            await admin.GetAsync(new Uri($"/api/v1/scans?trackingNumber={parcel.TrackingNumber}&outcome=Duplicate", UriKind.Relative)));
        Assert.Equal(1, duplicatesOnly.TotalCount);
    }

    [Fact]
    public async Task ABarcodeOrQrCodeText_FindsTheParcel()
    {
        var (parcel, branch, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);

        var qrText = $"http://localhost:5173/track/{parcel.TrackingNumber}";
        var result = await ReadAsync<ScanResultDto>(await hub.PostAsJsonAsync(ScanUri, new ScanRequest(qrText, "Arrival", warehouse)));
        var lookup = await ReadAsync<ParcelResponse>(
            await hub.GetAsync(new Uri($"/api/v1/parcels/lookup?code={Uri.EscapeDataString(qrText)}", UriKind.Relative)));

        Assert.Equal(parcel.TrackingNumber, result.TrackingNumber);
        Assert.Equal("Applied", result.Outcome);
        Assert.Equal(parcel.Id, lookup.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await hub.GetAsync(new Uri("/api/v1/parcels/lookup?code=nonsense", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task ParcelsMoveBetweenWarehouses_AndAnArrivalAtTheWrongOneIsFlagged()
    {
        var (parcel, branch, first) = await InTransitParcelAsync();
        var second = await _helpers.CreateWarehouseAsync(branch, "Kathmandu East Hub");
        var third = await _helpers.CreateWarehouseAsync(branch, "Kathmandu North Hub");
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);

        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", first))).EnsureSuccessStatusCode();
        var dispatch = await ReadAsync<ScanResultDto>(await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Dispatch", first, "To the east hub", second)));
        var arrival = await ReadAsync<ScanResultDto>(await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", second)));
        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Dispatch", second, ToWarehouseId: first))).EnsureSuccessStatusCode();
        var wrong = await ReadAsync<ScanResultDto>(await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", third)));

        Assert.Equal("InTransit", dispatch.Status);
        Assert.Equal("Kathmandu East Hub", dispatch.ToWarehouse);
        Assert.Equal("Applied", arrival.Outcome);
        Assert.Equal("Applied", wrong.Outcome);
        Assert.Contains("different warehouse", wrong.Message, StringComparison.Ordinal);

        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var history = await ReadAsync<List<ParcelMovementResponse>>(await admin.GetAsync(MovementsUri(parcel.Id)));
        Assert.Equal(["Arrival", "Dispatch", "Arrival", "Dispatch", "Arrival"], history.Select(m => m.ScanType));
        Assert.Equal(second, history[1].ToWarehouseId);
        Assert.Equal("Kathmandu East Hub", history[1].ToWarehouseName);
        Assert.Equal([false, false, false, false, true], history.Select(m => m.IsUnexpectedArrival));
        Assert.True(history.Zip(history.Skip(1), (a, b) => a.OccurredAtUtc <= b.OccurredAtUtc).All(ok => ok));
    }

    [Fact]
    public async Task ADispatchToTheSameWarehouse_Returns422()
    {
        var (parcel, branch, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);
        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse))).EnsureSuccessStatusCode();

        var response = await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Dispatch", warehouse, ToWarehouseId: warehouse));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AScanThatBreaksTheWorkflow_Returns422_AndAddsNothing()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var warehouse = await _helpers.CreateWarehouseAsync(branch);
        var parcel = await BookAsync(customer, NewBooking(branch));
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);

        var response = await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        Assert.Empty(await ReadAsync<List<ParcelMovementResponse>>(await admin.GetAsync(MovementsUri(parcel.Id))));
        var log = await ReadAsync<PagedResponse<ParcelScanResponse>>(await admin.GetAsync(new Uri($"/api/v1/scans?trackingNumber={parcel.TrackingNumber}", UriKind.Relative)));
        Assert.Equal(0, log.TotalCount);
    }

    [Theory]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.DeliveryAgent)]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.Customer)]
    public async Task OnlyWarehouseStaffAndManagers_CanScan(string role)
    {
        var (parcel, branch, warehouse) = await InTransitParcelAsync();
        var client = await _helpers.StaffAtAsync(role, branch);

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
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(ScanUri)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(MovementsUri(parcel.Id))).StatusCode);
    }

    [Fact]
    public async Task StaffWhoAreNotAssigned_OrAssignedElsewhere_CannotScanAtThisWarehouse()
    {
        var (parcel, branch, warehouse) = await InTransitParcelAsync();
        var otherBranch = await _helpers.CreateBranchAsync();
        var sibling = await _helpers.CreateWarehouseAsync(branch, "Kathmandu East Hub");
        var unassigned = await _helpers.StaffAsync(Roles.WarehouseStaff);
        var elsewhere = await _helpers.StaffAtAsync(Roles.WarehouseStaff, otherBranch);
        var siblingOnly = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch, sibling);
        var request = new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse);

        Assert.Equal(HttpStatusCode.Forbidden, (await unassigned.PostAsJsonAsync(ScanUri, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await elsewhere.PostAsJsonAsync(ScanUri, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await siblingOnly.PostAsJsonAsync(ScanUri, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await unassigned.PostAsJsonAsync(BatchUri, new BatchScanRequest("Arrival", warehouse, [parcel.TrackingNumber]))).StatusCode);

        var problem = await ReadAsync<JsonElement>(await unassigned.PostAsJsonAsync(ScanUri, request));
        Assert.Contains("not assigned", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // Nothing was recorded, and the person with the right assignment can still scan.
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        Assert.Equal("InTransit", (await ReadAsync<ParcelResponse>(await admin.GetAsync(ParcelUri(parcel.Id)))).Status);
        var local = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch, warehouse);
        Assert.Equal(HttpStatusCode.OK, (await local.PostAsJsonAsync(ScanUri, request)).StatusCode);
    }

    [Fact]
    public async Task ASuperAdministrator_CanScanAnywhere()
    {
        var (parcel, _, warehouse) = await InTransitParcelAsync();
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse))).StatusCode);
    }

    [Fact]
    public async Task AScan_OfAnUnknownParcel_OrWarehouse_Returns404_AndABadScanTypeReturns400()
    {
        var (parcel, branch, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);

        Assert.Equal(HttpStatusCode.NotFound, (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(TrackingNumbers.Generate(), "Arrival", warehouse))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Teleport", warehouse))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await hub.PostAsJsonAsync(ScanUri, new ScanRequest("", "Arrival", warehouse))).StatusCode);
    }

    [Fact]
    public async Task APublicTrackingPage_ShowsTheWarehouseName_AfterAScan()
    {
        var (parcel, branch, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);
        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse))).EnsureSuccessStatusCode();

        var tracking = await ReadAsync<PublicTrackingResponse>(
            await factory.CreateClient().GetAsync(new Uri($"/api/v1/tracking/{parcel.TrackingNumber}", UriKind.Relative)));

        Assert.Equal("ArrivedAtWarehouse", tracking.Status);
        Assert.Equal("Kathmandu Hub", tracking.Events[^1].Location);
    }

    [Fact]
    public async Task TheScanLog_CanBeFilteredByWarehouseTypeAndDate()
    {
        var (parcel, branch, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);
        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse))).EnsureSuccessStatusCode();
        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Dispatch", warehouse))).EnsureSuccessStatusCode();
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.75)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.75).AddDays(1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        async Task<int> CountAsync(string query) =>
            (await ReadAsync<PagedResponse<ParcelScanResponse>>(await admin.GetAsync(new Uri($"/api/v1/scans?warehouseId={warehouse}&{query}", UriKind.Relative)))).TotalCount;

        Assert.Equal(2, await CountAsync(string.Empty));
        Assert.Equal(1, await CountAsync("scanType=Dispatch"));
        Assert.Equal(2, await CountAsync($"from={today}&to={today}"));
        Assert.Equal(0, await CountAsync($"from={tomorrow}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(new Uri("/api/v1/scans?scanType=Teleport", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(new Uri($"/api/v1/scans?from={tomorrow}&to={today}", UriKind.Relative))).StatusCode);
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
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);
        var unknown = TrackingNumbers.Generate();

        var response = await hub.PostAsJsonAsync(
            BatchUri,
            new BatchScanRequest("Arrival", warehouse, [first.TrackingNumber, notShippedYet.TrackingNumber, unknown, "garbage", second.TrackingNumber, first.TrackingNumber]));
        var result = await ReadAsync<BatchScanResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, result.Results.Count); // the repeated number is scanned once
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(3, result.Failed);
        Assert.Equal("Applied", result.Results.Single(r => r.TrackingNumber == first.TrackingNumber).Outcome);
        Assert.Equal("ArrivedAtWarehouse", result.Results.Single(r => r.TrackingNumber == second.TrackingNumber).Status);
        var refused = result.Results.Single(r => r.TrackingNumber == notShippedYet.TrackingNumber);
        Assert.Equal("Failed", refused.Outcome);
        Assert.False(refused.Succeeded);
        Assert.Contains("cannot move to", refused.Error, StringComparison.Ordinal);
        Assert.Contains("was not found", result.Results.Single(r => r.TrackingNumber == unknown).Error, StringComparison.Ordinal);
        Assert.False(result.Results.Single(r => r.TrackingNumber == "GARBAGE").Succeeded);

        // The refused parcel was not touched; the good ones were saved.
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        Assert.Equal("BookingCreated", (await ReadAsync<ParcelResponse>(await admin.GetAsync(ParcelUri(notShippedYet.Id)))).Status);
        Assert.Equal("ArrivedAtWarehouse", (await ReadAsync<ParcelResponse>(await admin.GetAsync(ParcelUri(first.Id)))).Status);

        // Sending the very same batch again records nothing new: every parcel that arrived is reported as a duplicate.
        var again = await ReadAsync<BatchScanResponse>(
            await hub.PostAsJsonAsync(BatchUri, new BatchScanRequest("Arrival", warehouse, [first.TrackingNumber, second.TrackingNumber])));
        Assert.Equal(0, again.Succeeded);
        Assert.Equal(2, again.Duplicates);
        Assert.Single(await ReadAsync<List<ParcelMovementResponse>>(await admin.GetAsync(MovementsUri(first.Id))));
    }

    [Fact]
    public async Task ABatch_HasLimits_AndAnUnknownWarehouseFailsTheWholeThing()
    {
        var (parcel, branch, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);

        Assert.Equal(HttpStatusCode.BadRequest, (await hub.PostAsJsonAsync(BatchUri, new BatchScanRequest("Arrival", warehouse, []))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await hub.PostAsJsonAsync(BatchUri, new BatchScanRequest("Arrival", warehouse, Enumerable.Repeat(parcel.TrackingNumber, 101).ToList()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await hub.PostAsJsonAsync(BatchUri, new BatchScanRequest("Teleport", warehouse, [parcel.TrackingNumber]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hub.PostAsJsonAsync(BatchUri, new BatchScanRequest("Arrival", Guid.NewGuid(), [parcel.TrackingNumber]))).StatusCode);

        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        Assert.Equal("InTransit", (await ReadAsync<ParcelResponse>(await admin.GetAsync(ParcelUri(parcel.Id)))).Status);
    }

    [Fact]
    public async Task ABatchOfOneHundred_IsAccepted()
    {
        var (_, branch, warehouse) = await InTransitParcelAsync();
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);
        var numbers = Enumerable.Range(0, 100).Select(_ => TrackingNumbers.Generate()).ToList();

        var response = await hub.PostAsJsonAsync(BatchUri, new BatchScanRequest("Arrival", warehouse, numbers));
        var result = await ReadAsync<BatchScanResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(100, result.Failed);
        Assert.All(result.Results, r => Assert.False(string.IsNullOrWhiteSpace(r.Error)));
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("results").ValueKind);
    }
}
