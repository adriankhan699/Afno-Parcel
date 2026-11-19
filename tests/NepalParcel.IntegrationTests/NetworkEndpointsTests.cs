using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Branches;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Contracts.Staff;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Contracts.Warehouses;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Warehouses;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;

namespace NepalParcel.IntegrationTests;

/// <summary>Phase 5: managing branches and staff, warehouse inventory, waiting lists, labels and the append-only ledgers.</summary>
public sealed class NetworkEndpointsTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private static readonly Uri ScanUri = new("/api/v1/scans", UriKind.Relative);
    private static readonly Uri WaitingUri = new("/api/v1/inventory/waiting", UriKind.Relative);
    private readonly BookingTestHelpers _helpers = new(factory);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>())!;

    private static Uri BranchUri(Guid id, string suffix = "") => new($"/api/v1/branches/{id}{suffix}", UriKind.Relative);

    private static Uri WarehouseUri(Guid id, string suffix = "") => new($"/api/v1/warehouses/{id}{suffix}", UriKind.Relative);

    private static Uri AssignmentUri(Guid userId) => new($"/api/v1/staff/{userId}/assignment", UriKind.Relative);

    private async Task<Guid> NewBranchAsync(HttpClient admin, string name = "Phase5 Branch")
    {
        var response = await admin.PostAsJsonAsync(
            new Uri("/api/v1/branches", UriKind.Relative),
            new CreateBranchRequest("P" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), name, "Branch", KathmanduAddress, "01-5523456"));
        response.EnsureSuccessStatusCode();
        return (await ReadAsync<BranchResponse>(response)).Id;
    }

    // ---- branches ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABranch_CanBeEdited_ButNotItsCodeOrType()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var id = await NewBranchAsync(admin, "Old Name");
        var before = await ReadAsync<BranchResponse>(await admin.GetAsync(BranchUri(id)));

        var response = await admin.PutAsJsonAsync(BranchUri(id), new UpdateBranchRequest("New Name", PokharaAddress, "9841234500", "new@example.test"));
        var after = await ReadAsync<BranchResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("New Name", after.Name);
        Assert.Equal("9841234500", after.PhoneNumber);
        Assert.Equal("new@example.test", after.Email);
        Assert.Equal("Kaski", after.Address.District);
        Assert.Equal(before.Code, after.Code);
        Assert.Equal(before.Type, after.Type);
        Assert.NotNull(after.UpdatedAtUtc);
    }

    [Fact]
    public async Task EditingABranch_ValidatesTheRequest_AndNeedsAManager()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var id = await NewBranchAsync(admin);
        var valid = new UpdateBranchRequest("Fine Name", KathmanduAddress, "9841234500");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(BranchUri(id), valid with { PhoneNumber = "123" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync(BranchUri(Guid.NewGuid()), valid)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _helpers.StaffAsync(Roles.WarehouseStaff)).PutAsJsonAsync(BranchUri(id), valid)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PutAsJsonAsync(BranchUri(id), valid)).StatusCode);
    }

    [Fact]
    public async Task ABranchManager_CanEditOnlyTheirOwnBranch()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var own = await NewBranchAsync(admin);
        var other = await NewBranchAsync(admin);
        var manager = await _helpers.StaffAtAsync(Roles.BranchManager, own);
        var unassigned = await _helpers.StaffAsync(Roles.BranchManager);
        var change = new UpdateBranchRequest("Managed", KathmanduAddress, "9841234500");

        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(BranchUri(own), change)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync(BranchUri(other), change)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync(BranchUri(other, "/deactivate"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await unassigned.PutAsJsonAsync(BranchUri(own), change)).StatusCode);
    }

    [Fact]
    public async Task ABranch_CanBeSwitchedOffAndOn_AndInactiveOnesAreHiddenFromEveryoneButManagers()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var id = await NewBranchAsync(admin, "Switch Me Off");

        var off = await admin.PostAsync(BranchUri(id, "/deactivate"), null);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await ReadAsync<BranchResponse>(off)).IsActive);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(BranchUri(id, "/deactivate"), null)).StatusCode);

        var publicList = await ReadAsync<PagedResponse<BranchResponse>>(await factory.CreateClient().GetAsync(new Uri("/api/v1/branches?pageSize=100&search=Switch", UriKind.Relative)));
        var customerWantsAll = await ReadAsync<PagedResponse<BranchResponse>>(
            await (await _helpers.CustomerAsync()).GetAsync(new Uri("/api/v1/branches?pageSize=100&search=Switch&includeInactive=true", UriKind.Relative)));
        var managerList = await ReadAsync<PagedResponse<BranchResponse>>(await admin.GetAsync(new Uri("/api/v1/branches?pageSize=100&search=Switch&includeInactive=true", UriKind.Relative)));
        Assert.DoesNotContain(publicList.Items, b => b.Id == id);
        Assert.DoesNotContain(customerWantsAll.Items, b => b.Id == id);
        Assert.Contains(managerList.Items, b => b.Id == id && !b.IsActive);

        var on = await admin.PostAsync(BranchUri(id, "/activate"), null);
        Assert.True((await ReadAsync<BranchResponse>(on)).IsActive);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(BranchUri(id, "/activate"), null)).StatusCode);
    }

    [Fact]
    public async Task ABranch_CannotBeSwitchedOff_WhileItStillHasWarehousesStaffOrParcels()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);

        var withWarehouse = await NewBranchAsync(admin);
        var warehouse = await _helpers.CreateWarehouseAsync(withWarehouse);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync(BranchUri(withWarehouse, "/deactivate"), null)).StatusCode);
        (await admin.PostAsync(WarehouseUri(warehouse, "/deactivate"), null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(BranchUri(withWarehouse, "/deactivate"), null)).StatusCode);

        var withStaff = await NewBranchAsync(admin);
        var worker = await _helpers.StaffAtAsync(Roles.BookingStaff, withStaff);
        Assert.NotNull(worker);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync(BranchUri(withStaff, "/deactivate"), null)).StatusCode);

        var withParcel = await NewBranchAsync(admin);
        await BookAsync(await _helpers.CustomerAsync(), NewBooking(withParcel));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync(BranchUri(withParcel, "/deactivate"), null)).StatusCode);
    }

    [Fact]
    public async Task TheBranchList_ShowsWarehouseAndStaffCounts_AndCanBeFiltered()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var name = NewName("Counted");
        var id = await NewBranchAsync(admin, name);
        await _helpers.CreateWarehouseAsync(id);
        await _helpers.CreateWarehouseAsync(id);
        await _helpers.StaffAtAsync(Roles.BookingStaff, id);

        var found = await ReadAsync<PagedResponse<BranchResponse>>(await admin.GetAsync(new Uri($"/api/v1/branches?search={name}&type=Branch", UriKind.Relative)));
        var headOffices = await ReadAsync<PagedResponse<BranchResponse>>(await admin.GetAsync(new Uri($"/api/v1/branches?search={name}&type=HeadOffice", UriKind.Relative)));

        var branch = Assert.Single(found.Items);
        Assert.Equal(2, branch.WarehouseCount);
        Assert.Equal(1, branch.StaffCount);
        Assert.Empty(headOffices.Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(new Uri("/api/v1/branches?type=Depot", UriKind.Relative))).StatusCode);
    }

    // ---- staff assignments ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AdminsAssignStaff_ToABranchAndAWarehouse_AndTheyCanSeeWhereTheyWork()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var branch = await NewBranchAsync(admin, "Assigned Branch");
        var warehouse = await _helpers.CreateWarehouseAsync(branch, "Assigned Hub");
        var (worker, workerId) = await TestUsers.CreateClientWithIdAsync(factory, Roles.WarehouseStaff);
        Assert.Null((await ReadAsync<MyAssignmentResponse>(await worker.GetAsync(new Uri("/api/v1/staff/me/assignment", UriKind.Relative)))).Assignment);

        var response = await admin.PutAsJsonAsync(AssignmentUri(workerId), new AssignStaffRequest(branch, warehouse));
        var assigned = await ReadAsync<StaffMemberResponse>(response);
        var mine = (await ReadAsync<MyAssignmentResponse>(await worker.GetAsync(new Uri("/api/v1/staff/me/assignment", UriKind.Relative)))).Assignment!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(workerId, assigned.UserId);
        Assert.Equal("Assigned Branch", assigned.Assignment!.BranchName);
        Assert.Equal("Assigned Hub", assigned.Assignment.WarehouseName);
        Assert.Equal(branch, mine.BranchId);
        Assert.Equal(warehouse, mine.WarehouseId);
    }

    [Fact]
    public async Task Reassigning_EndsTheOldAssignment_ButKeepsItAsHistory()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var first = await NewBranchAsync(admin);
        var second = await NewBranchAsync(admin);
        var (_, workerId) = await TestUsers.CreateClientWithIdAsync(factory, Roles.BookingStaff);

        (await admin.PutAsJsonAsync(AssignmentUri(workerId), new AssignStaffRequest(first))).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync(AssignmentUri(workerId), new AssignStaffRequest(second))).EnsureSuccessStatusCode();
        var current = await ReadAsync<StaffMemberResponse>(await admin.PutAsJsonAsync(AssignmentUri(workerId), new AssignStaffRequest(second)));

        Assert.Equal(second, current.Assignment!.BranchId);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var rows = await db.StaffAssignments.AsNoTracking().Where(a => a.UserId == workerId).OrderBy(a => a.AssignedAtUtc).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].EndedAtUtc);
        Assert.Equal(first, rows[0].BranchId);
        Assert.Null(rows[1].EndedAtUtc);
        Assert.Equal(1, rows.Count(r => r.EndedAtUtc == null));
    }

    [Fact]
    public async Task AStaffMember_CanBeUnassigned()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var branch = await NewBranchAsync(admin);
        var (worker, workerId) = await TestUsers.CreateClientWithIdAsync(factory, Roles.WarehouseStaff);
        (await admin.PutAsJsonAsync(AssignmentUri(workerId), new AssignStaffRequest(branch))).EnsureSuccessStatusCode();

        var response = await admin.DeleteAsync(AssignmentUri(workerId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null((await ReadAsync<MyAssignmentResponse>(await worker.GetAsync(new Uri("/api/v1/staff/me/assignment", UriKind.Relative)))).Assignment);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync(AssignmentUri(workerId))).StatusCode);
    }

    [Fact]
    public async Task Assigning_RefusesCustomers_UnknownPeople_AndMismatchedWarehouses()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var branch = await NewBranchAsync(admin);
        var otherBranch = await NewBranchAsync(admin);
        var foreignWarehouse = await _helpers.CreateWarehouseAsync(otherBranch);
        var (_, customerId) = await TestUsers.CreateClientWithIdAsync(factory, Roles.Customer);
        var (_, staffId) = await TestUsers.CreateClientWithIdAsync(factory, Roles.WarehouseStaff);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PutAsJsonAsync(AssignmentUri(customerId), new AssignStaffRequest(branch))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync(AssignmentUri(Guid.NewGuid()), new AssignStaffRequest(branch))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync(AssignmentUri(staffId), new AssignStaffRequest(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PutAsJsonAsync(AssignmentUri(staffId), new AssignStaffRequest(branch, foreignWarehouse))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(AssignmentUri(staffId), new AssignStaffRequest(Guid.Empty))).StatusCode);
    }

    [Fact]
    public async Task ABranchManager_AssignsStaffOnlyWithinTheirOwnBranch()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var own = await NewBranchAsync(admin);
        var other = await NewBranchAsync(admin);
        var manager = await _helpers.StaffAtAsync(Roles.BranchManager, own);
        var (_, newcomer) = await TestUsers.CreateClientWithIdAsync(factory, Roles.WarehouseStaff);
        var (_, abroad) = await TestUsers.CreateClientWithIdAsync(factory, Roles.WarehouseStaff);
        (await admin.PutAsJsonAsync(AssignmentUri(abroad), new AssignStaffRequest(other))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(AssignmentUri(newcomer), new AssignStaffRequest(own))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync(AssignmentUri(newcomer), new AssignStaffRequest(other))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync(AssignmentUri(abroad), new AssignStaffRequest(own))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync(AssignmentUri(abroad))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync(AssignmentUri(newcomer))).StatusCode);
    }

    [Theory]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.Customer)]
    public async Task OnlyManagers_CanAssignOrListStaff(string role)
    {
        var client = await _helpers.StaffAsync(role);
        var (_, target) = await TestUsers.CreateClientWithIdAsync(factory, Roles.WarehouseStaff);
        var branch = await NewBranchAsync(await _helpers.StaffAsync(Roles.SuperAdmin));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(AssignmentUri(target), new AssignStaffRequest(branch))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri("/api/v1/staff", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(new Uri("/api/v1/staff", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task TheStaffList_ShowsWhereEachPersonWorks_AndCanBeFiltered()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var branch = await NewBranchAsync(admin);
        var (_, workerId) = await TestUsers.CreateClientWithIdAsync(factory, Roles.WarehouseStaff);
        var (_, idleId) = await TestUsers.CreateClientWithIdAsync(factory, Roles.WarehouseStaff);
        (await admin.PutAsJsonAsync(AssignmentUri(workerId), new AssignStaffRequest(branch))).EnsureSuccessStatusCode();

        var atBranch = await ReadAsync<PagedResponse<StaffMemberResponse>>(await admin.GetAsync(new Uri($"/api/v1/staff?branchId={branch}", UriKind.Relative)));
        var unassigned = await ReadAsync<PagedResponse<StaffMemberResponse>>(await admin.GetAsync(new Uri("/api/v1/staff?unassigned=true&pageSize=100", UriKind.Relative)));
        var byRole = await ReadAsync<PagedResponse<StaffMemberResponse>>(await admin.GetAsync(new Uri("/api/v1/staff?role=Accountant&pageSize=100", UriKind.Relative)));

        var member = Assert.Single(atBranch.Items);
        Assert.Equal(workerId, member.UserId);
        Assert.Contains("WarehouseStaff", member.Roles);
        Assert.Equal(branch, member.Assignment!.BranchId);
        Assert.Contains(unassigned.Items, s => s.UserId == idleId && s.Assignment is null);
        Assert.DoesNotContain(unassigned.Items, s => s.UserId == workerId);
        Assert.All(byRole.Items, s => Assert.Contains("Accountant", s.Roles));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(new Uri("/api/v1/staff?role=Wizard", UriKind.Relative))).StatusCode);
    }

    // ---- inventory -----------------------------------------------------------------------------------------------

    private async Task<(ParcelResponse Parcel, Guid Branch, Guid Warehouse, HttpClient Hub)> ParcelAtWarehouseAsync(Guid? branch = null, Guid? warehouse = null)
    {
        var customer = await _helpers.CustomerAsync();
        var branchId = branch ?? await _helpers.CreateBranchAsync();
        var warehouseId = warehouse ?? await _helpers.CreateWarehouseAsync(branchId, "Inventory Hub");
        var parcel = await BookAsync(customer, NewBooking(branchId));
        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.InTransit);
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branchId);
        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouseId))).EnsureSuccessStatusCode();
        return (parcel, branchId, warehouseId, hub);
    }

    [Fact]
    public async Task TheWarehouseInventory_ListsWhatItHolds_AndWhatIsOnItsWay()
    {
        var (held, branch, warehouse, hub) = await ParcelAtWarehouseAsync();
        var other = await _helpers.CreateWarehouseAsync(branch, "Next Hub");
        var travelling = await BookAsync(await _helpers.CustomerAsync(), NewBooking(branch));
        await _helpers.AdvanceAsync(travelling.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.InTransit);
        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(travelling.TrackingNumber, "Arrival", warehouse))).EnsureSuccessStatusCode();
        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(travelling.TrackingNumber, "Dispatch", warehouse, ToWarehouseId: other))).EnsureSuccessStatusCode();
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);

        var here = await ReadAsync<WarehouseInventoryResponse>(await admin.GetAsync(WarehouseUri(warehouse, "/inventory")));
        var there = await ReadAsync<WarehouseInventoryResponse>(await admin.GetAsync(WarehouseUri(other, "/inventory")));

        Assert.Equal("Inventory Hub", here.WarehouseName);
        Assert.Equal(1, here.ParcelsHeld);
        Assert.Equal(0, here.ParcelsInbound);
        var row = Assert.Single(here.Held.Items);
        Assert.Equal(held.TrackingNumber, row.TrackingNumber);
        Assert.Equal("ArrivedAtWarehouse", row.Status);
        Assert.True(row.WaitingHours >= 0);
        Assert.Equal(0, there.ParcelsHeld);
        Assert.Equal(1, there.ParcelsInbound);
        Assert.Equal(travelling.TrackingNumber, Assert.Single(there.Inbound).TrackingNumber);

        // The listed counts on the warehouse itself agree.
        var summary = await ReadAsync<WarehouseResponse>(await admin.GetAsync(WarehouseUri(other)));
        Assert.Equal(1, summary.ParcelsInbound);
    }

    [Fact]
    public async Task TheWarehouseInventory_CanBeSearched_AndShowsHowFullTheWarehouseIs()
    {
        var (parcel, _, warehouse, _) = await ParcelAtWarehouseAsync();
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);

        var match = await ReadAsync<WarehouseInventoryResponse>(await admin.GetAsync(WarehouseUri(warehouse, $"/inventory?search={parcel.TrackingNumber[3..9]}")));
        var miss = await ReadAsync<WarehouseInventoryResponse>(await admin.GetAsync(WarehouseUri(warehouse, "/inventory?search=ZZZZZZZZ")));

        Assert.Single(match.Held.Items);
        Assert.Empty(miss.Held.Items);
        Assert.Equal(5000, match.CapacityParcels);
        Assert.Equal(0.0m, match.UtilisationPercent); // one of 5000 rounds to 0.0
        Assert.Equal(1, match.ParcelsHeld);
    }

    [Fact]
    public async Task TheWarehouseInventory_IsForStaffWhoWorkWithParcels()
    {
        var (_, _, warehouse, _) = await ParcelAtWarehouseAsync();

        Assert.Equal(HttpStatusCode.OK, (await (await _helpers.StaffAsync(Roles.WarehouseStaff)).GetAsync(WarehouseUri(warehouse, "/inventory"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await _helpers.StaffAsync(Roles.BranchManager)).GetAsync(WarehouseUri(warehouse, "/inventory"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _helpers.CustomerAsync()).GetAsync(WarehouseUri(warehouse, "/inventory"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _helpers.StaffAsync(Roles.DeliveryAgent)).GetAsync(WarehouseUri(warehouse, "/inventory"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(WarehouseUri(warehouse, "/inventory"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await _helpers.StaffAsync(Roles.SuperAdmin)).GetAsync(WarehouseUri(Guid.NewGuid(), "/inventory"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await (await _helpers.StaffAsync(Roles.SuperAdmin)).GetAsync(WarehouseUri(warehouse, "/inventory?page=0"))).StatusCode);
    }

    // ---- waiting for pickup or dispatch --------------------------------------------------------------------------

    [Fact]
    public async Task ParcelsWaitingForPickup_AndForDispatch_AreListedSeparately_LongestWaitingFirst()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var branch = await NewBranchAsync(admin, "Waiting Branch");
        var customer = await _helpers.CustomerAsync();
        var booked = await BookAsync(customer, NewBooking(branch));
        var pickedUp = await BookAsync(customer, NewBooking(branch));
        await _helpers.AdvanceAsync(pickedUp.Id, ParcelStatusCode.PickedUp);
        var delivered = await BookAsync(customer, NewBooking(branch));
        await _helpers.AdvanceAsync(delivered.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.OutForDelivery, ParcelStatusCode.Delivered);

        var pickup = await ReadAsync<PagedResponse<WaitingParcelDto>>(await admin.GetAsync(new Uri($"{WaitingUri}?kind=Pickup&branchId={branch}", UriKind.Relative)));
        var dispatch = await ReadAsync<PagedResponse<WaitingParcelDto>>(await admin.GetAsync(new Uri($"{WaitingUri}?kind=Dispatch&branchId={branch}", UriKind.Relative)));
        var both = await ReadAsync<PagedResponse<WaitingParcelDto>>(await admin.GetAsync(new Uri($"{WaitingUri}?branchId={branch}", UriKind.Relative)));

        var waitingForPickup = Assert.Single(pickup.Items);
        Assert.Equal(booked.TrackingNumber, waitingForPickup.TrackingNumber);
        Assert.Equal("Pickup", waitingForPickup.Kind);
        Assert.Equal("Waiting Branch", waitingForPickup.Location);
        var waitingForDispatch = Assert.Single(dispatch.Items);
        Assert.Equal(pickedUp.TrackingNumber, waitingForDispatch.TrackingNumber);
        Assert.Equal("Dispatch", waitingForDispatch.Kind);
        Assert.Equal(2, both.TotalCount);
        Assert.DoesNotContain(both.Items, p => p.TrackingNumber == delivered.TrackingNumber);
    }

    [Fact]
    public async Task ParcelsSittingAtAWarehouse_AreWaitingForDispatch_UntilTheyLeave()
    {
        var (parcel, _, warehouse, hub) = await ParcelAtWarehouseAsync();
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var uri = new Uri($"{WaitingUri}?kind=Dispatch&warehouseId={warehouse}", UriKind.Relative);

        var waiting = await ReadAsync<PagedResponse<WaitingParcelDto>>(await admin.GetAsync(uri));
        Assert.Equal(parcel.TrackingNumber, Assert.Single(waiting.Items).TrackingNumber);
        Assert.Equal("Inventory Hub", waiting.Items[0].Location);

        (await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Dispatch", warehouse))).EnsureSuccessStatusCode();
        Assert.Empty((await ReadAsync<PagedResponse<WaitingParcelDto>>(await admin.GetAsync(uri))).Items);
    }

    [Fact]
    public async Task TheWaitingList_ValidatesItsFilters_AndIsForStaffOnly()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await (await _helpers.StaffAsync(Roles.SuperAdmin)).GetAsync(new Uri($"{WaitingUri}?kind=Someday", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _helpers.CustomerAsync()).GetAsync(WaitingUri)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(WaitingUri)).StatusCode);
    }

    // ---- labels ----------------------------------------------------------------------------------------------------

    private static string DecodeSvg(string dataUri)
    {
        const string prefix = "data:image/svg+xml;base64,";
        Assert.StartsWith(prefix, dataUri, StringComparison.Ordinal);
        return Encoding.UTF8.GetString(Convert.FromBase64String(dataUri[prefix.Length..]));
    }

    [Fact]
    public async Task TheLabel_HasABarcodeAndAQrCode_ForTheTrackingNumber()
    {
        var branch = await _helpers.CreateBranchAsync();
        var customer = await _helpers.CustomerAsync();
        var parcel = await BookAsync(customer, NewBooking(branch));
        var other = await BookAsync(customer, NewBooking(branch));

        var label = await ReadAsync<ParcelLabelResponse>(await customer.GetAsync(new Uri($"/api/v1/parcels/{parcel.Id}/label", UriKind.Relative)));
        var otherLabel = await ReadAsync<ParcelLabelResponse>(await customer.GetAsync(new Uri($"/api/v1/parcels/{other.Id}/label", UriKind.Relative)));

        Assert.Equal(parcel.TrackingNumber, label.TrackingNumber);
        Assert.EndsWith($"/track/{parcel.TrackingNumber}", label.QrContent, StringComparison.Ordinal);
        Assert.StartsWith("http", label.QrContent, StringComparison.Ordinal);
        var barcode = DecodeSvg(label.BarcodeDataUri);
        var qr = DecodeSvg(label.QrDataUri);
        Assert.Contains("<svg", barcode, StringComparison.Ordinal);
        Assert.Contains("<svg", qr, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", barcode + qr, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(barcode, DecodeSvg(otherLabel.BarcodeDataUri));
        Assert.NotEqual(qr, DecodeSvg(otherLabel.QrDataUri));
        Assert.Equal("Standard", label.ServiceType);
        Assert.Equal(2, label.Quantity);
        Assert.Equal("Sita Gurung", label.ReceiverName);
    }

    [Fact]
    public async Task TheLabel_DoesNotRevealPhoneNumbers()
    {
        var branch = await _helpers.CreateBranchAsync();
        var customer = await _helpers.CustomerAsync();
        var parcel = await BookAsync(customer, NewBooking(branch, receiverPhone: "9851234567"));

        var json = await (await customer.GetAsync(new Uri($"/api/v1/parcels/{parcel.Id}/label", UriKind.Relative))).Content.ReadAsStringAsync();

        Assert.DoesNotContain("9851234567", json, StringComparison.Ordinal);
        Assert.DoesNotContain("9841234567", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheSenderAndStaff_CanGetALabel()
    {
        var branch = await _helpers.CreateBranchAsync();
        var parcel = await BookAsync(await _helpers.CustomerAsync(), NewBooking(branch));
        var uri = new Uri($"/api/v1/parcels/{parcel.Id}/label", UriKind.Relative);

        Assert.Equal(HttpStatusCode.OK, (await (await _helpers.StaffAsync(Roles.WarehouseStaff)).GetAsync(uri)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await _helpers.CustomerAsync()).GetAsync(uri)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(uri)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await _helpers.StaffAsync(Roles.SuperAdmin)).GetAsync(new Uri($"/api/v1/parcels/{Guid.NewGuid()}/label", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task TheScannedCode_FindsTheParcel_ForStaffOnly()
    {
        var branch = await _helpers.CreateBranchAsync();
        var customer = await _helpers.CustomerAsync();
        var parcel = await BookAsync(customer, NewBooking(branch));
        var uri = new Uri($"/api/v1/parcels/lookup?code={parcel.TrackingNumber.ToLowerInvariant()}", UriKind.Relative);

        Assert.Equal(HttpStatusCode.OK, (await (await _helpers.StaffAsync(Roles.WarehouseStaff)).GetAsync(uri)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(uri)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(uri)).StatusCode);
    }

    // ---- the ledgers are append-only ----------------------------------------------------------------------------------

    [Fact]
    public async Task Movements_AndScans_CanNeverBeEditedOrDeleted()
    {
        var (parcel, _, _, _) = await ParcelAtWarehouseAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();

        var scan = await db.ParcelScans.SingleAsync(s => s.ParcelId == parcel.Id);
        db.ParcelScans.Remove(scan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var movement = await db.ParcelMovements.SingleAsync(m => m.ParcelId == parcel.Id);
        db.Entry(movement).Property(m => m.Remarks).CurrentValue = "rewritten";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.ParcelMovements.Remove(await db.ParcelMovements.SingleAsync(m => m.ParcelId == parcel.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        Assert.Equal(1, await db.ParcelMovements.CountAsync(m => m.ParcelId == parcel.Id));
        Assert.Equal(1, await db.ParcelScans.CountAsync(s => s.ParcelId == parcel.Id));
    }

    [Fact]
    public async Task TheDatabase_RefusesTwoActiveAssignmentsForOnePerson()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var branch = await NewBranchAsync(admin);
        var (_, userId) = await TestUsers.CreateClientWithIdAsync(factory, Roles.WarehouseStaff);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        db.StaffAssignments.Add(NepalParcel.Domain.Branches.StaffAssignment.Create(userId, branch, null, DateTimeOffset.UtcNow, null));
        await db.SaveChangesAsync();
        db.StaffAssignments.Add(NepalParcel.Domain.Branches.StaffAssignment.Create(userId, branch, null, DateTimeOffset.UtcNow, null));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ADuplicateScan_NeverLeavesTwoMovementsBehind_EvenWhenSentTogether()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var warehouse = await _helpers.CreateWarehouseAsync(branch);
        var parcel = await BookAsync(customer, NewBooking(branch));
        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.InTransit);
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);

        var responses = new List<HttpStatusCode>();
        foreach (var _ in Enumerable.Range(0, 3))
        {
            responses.Add((await hub.PostAsJsonAsync(ScanUri, new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse))).StatusCode);
        }

        Assert.All(responses, status => Assert.Equal(HttpStatusCode.OK, status));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        Assert.Equal(1, await db.ParcelMovements.CountAsync(m => m.ParcelId == parcel.Id));
        Assert.Equal(3, await db.ParcelScans.CountAsync(s => s.ParcelId == parcel.Id));
        Assert.Equal(2, await db.ParcelScans.CountAsync(s => s.ParcelId == parcel.Id && s.Outcome == ScanOutcome.Duplicate));
        Assert.Equal(1, await db.Set<ParcelStatusHistory>().CountAsync(h => h.ParcelId == parcel.Id && h.ToStatusId == (int)ParcelStatusCode.ArrivedAtWarehouse));
    }

    [Fact]
    public async Task TheStatusApi_AlsoRecordsTheMovement_WhenItIsUsedAtAWarehouse()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var warehouse = await _helpers.CreateWarehouseAsync(branch);
        var parcel = await BookAsync(customer, NewBooking(branch));
        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.InTransit);
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);

        (await hub.PostAsJsonAsync(StatusUri(parcel.Id), To("ArrivedAtWarehouse", warehouseId: warehouse))).EnsureSuccessStatusCode();

        var movements = await ReadAsync<List<ParcelMovementResponse>>(await hub.GetAsync(new Uri($"/api/v1/parcels/{parcel.Id}/movements", UriKind.Relative)));
        Assert.Equal("Arrival", Assert.Single(movements).ScanType);
        var unassigned = await _helpers.StaffAsync(Roles.WarehouseStaff);
        var second = await BookAsync(customer, NewBooking(branch));
        await _helpers.AdvanceAsync(second.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.InTransit);
        Assert.Equal(HttpStatusCode.Forbidden, (await unassigned.PostAsJsonAsync(StatusUri(second.Id), To("ArrivedAtWarehouse", warehouseId: warehouse))).StatusCode);
    }

    [Fact]
    public async Task ScansAndMovements_AreWrittenToTheAuditTrailOfStatusChanges()
    {
        var (parcel, _, _, _) = await ParcelAtWarehouseAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();

        var entries = await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == parcel.Id.ToString()).ToListAsync();

        Assert.Contains(entries, e => e.Action == NepalParcel.Domain.Auditing.AuditActions.ParcelStatusChanged);
        Assert.All(entries, e => Assert.NotNull(e.UserId));
        Assert.Contains("ArrivedAtWarehouse", entries.Last().NewValues, StringComparison.Ordinal);
        _ = JsonDocument.Parse(entries.Last().NewValues!);
    }
}
