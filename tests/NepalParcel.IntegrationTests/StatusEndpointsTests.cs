using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;

namespace NepalParcel.IntegrationTests;

/// <summary>Controlled status changes, who may make them, and the append-only history they leave behind.</summary>
public sealed class StatusEndpointsTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private readonly BookingTestHelpers _helpers = new(factory);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>())!;

    private static Uri ParcelUri(Guid id) => new($"/api/v1/parcels/{id}", UriKind.Relative);

    /// <summary>A parcel booked by a customer, plus the customer's client.</summary>
    private async Task<(ParcelResponse Parcel, HttpClient Customer, Guid Branch)> NewParcelAsync()
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        return (await BookAsync(customer, NewBooking(branch)), customer, branch);
    }

    // ---- the whole journey -------------------------------------------------------------------------------------

    [Fact]
    public async Task AParcel_CanBeTakenAllTheWayToDelivered_ByTheRightPeople_WithACompleteHistory()
    {
        var (parcel, customer, branch) = await NewParcelAsync();
        var warehouse = await _helpers.CreateWarehouseAsync(branch, "Kathmandu Hub");
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);
        var hub = await _helpers.StaffAsync(Roles.WarehouseStaff);
        var rider = await _helpers.StaffAsync(Roles.DeliveryAgent);

        var picked = await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("PickedUp", "Collected at the counter"));
        var shipped = await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("Shipped", "Loaded on the morning truck", branchId: branch));
        var transit = await hub.PostAsJsonAsync(StatusUri(parcel.Id), To("InTransit"));
        var arrived = await hub.PostAsJsonAsync(StatusUri(parcel.Id), To("ArrivedAtWarehouse", "Sorted into the Pokhara bag", warehouseId: warehouse));
        var outFor = await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("OutForDelivery", branchId: branch));
        var delivered = await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("Delivered", "Handed to the receiver"));

        foreach (var response in new[] { picked, shipped, transit, arrived, outFor, delivered })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var final = await ReadAsync<ParcelStatusUpdateResponse>(delivered);
        Assert.Equal("OutForDelivery", final.PreviousStatus);
        Assert.Equal("Delivered", final.Status);
        Assert.Equal("Delivered", final.StatusName);
        Assert.Empty(final.NextStatuses);
        Assert.Equal("Kathmandu Hub", (await ReadAsync<ParcelStatusUpdateResponse>(arrived)).Location);

        // What staff see: every step, who made it, where, and why.
        var staffView = await ReadAsync<ParcelResponse>(await counter.GetAsync(ParcelUri(parcel.Id)));
        Assert.Equal("Delivered", staffView.Status);
        Assert.NotNull(staffView.DeliveredAtUtc);
        Assert.Equal(
            ["BookingCreated", "PickedUp", "Shipped", "InTransit", "ArrivedAtWarehouse", "OutForDelivery", "Delivered"],
            staffView.History.Select(h => h.Status).ToArray());
        Assert.Equal(
            new string?[] { null, "BookingCreated", "PickedUp", "Shipped", "InTransit", "ArrivedAtWarehouse", "OutForDelivery" },
            staffView.History.Select(h => h.FromStatus).ToArray());
        Assert.Equal("Collected at the counter", staffView.History[1].Remarks);
        Assert.Equal("Test Branch", staffView.History[2].Location);
        Assert.Equal("Kathmandu Hub", staffView.History[4].Location);
        Assert.All(staffView.History.Skip(1), h => Assert.False(string.IsNullOrWhiteSpace(h.ChangedBy)));
        Assert.Empty(staffView.AllowedNextStatuses);
        Assert.Equal(staffView.History.OrderBy(h => h.OccurredAtUtc).Select(h => h.Status), staffView.History.Select(h => h.Status));

        // What the sender sees: the same journey, but never which employee did what.
        var customerView = await ReadAsync<ParcelResponse>(await customer.GetAsync(ParcelUri(parcel.Id)));
        Assert.Equal(7, customerView.History.Count);
        Assert.All(customerView.History, h => Assert.Null(h.ChangedBy));
        Assert.Equal("Kathmandu Hub", customerView.History[4].Location);
    }

    [Fact]
    public async Task EveryHistoryRow_StoresPreviousStatus_NewStatus_StaffUserId_Place_Timestamp_AndRemarks()
    {
        var (parcel, _, branch) = await NewParcelAsync();
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        (await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("PickedUp", "Collected by Ram"))).EnsureSuccessStatusCode();
        (await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("Shipped", "On truck 12", branchId: branch))).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var rows = (await db.ParcelStatusHistory.AsNoTracking().Where(h => h.ParcelId == parcel.Id).ToListAsync())
            .OrderBy(h => h.OccurredAtUtc)
            .ToList();

        Assert.Equal(3, rows.Count);
        var shipped = rows[2];
        Assert.Equal((int)ParcelStatusCode.PickedUp, shipped.FromStatusId);
        Assert.Equal((int)ParcelStatusCode.Shipped, shipped.ToStatusId);
        Assert.NotNull(shipped.ChangedByUserId);
        Assert.NotEqual(Guid.Empty, shipped.ChangedByUserId);
        Assert.Equal(branch, shipped.BranchId);
        Assert.Null(shipped.WarehouseId);
        Assert.True(shipped.OccurredAtUtc > before);
        Assert.Equal("On truck 12", shipped.Remarks);
        Assert.Equal(rows[1].ChangedByUserId, shipped.ChangedByUserId); // the same staff user made both changes
        Assert.Equal(parcel.Id, shipped.ParcelId);
    }

    [Fact]
    public async Task UpdatingByTrackingNumber_Works_ForStaffWhoOnlyHaveTheLabel()
    {
        var (parcel, _, _) = await NewParcelAsync();
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);

        var response = await counter.PostAsJsonAsync(
            new Uri($"/api/v1/parcels/tracking/{parcel.TrackingNumber.ToLowerInvariant()}/status", UriKind.Relative),
            To("PickedUp"));
        var update = await ReadAsync<ParcelStatusUpdateResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(parcel.Id, update.ParcelId);
        Assert.Equal("PickedUp", update.Status);
    }

    // ---- invalid transitions -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Delivered")]
    [InlineData("OutForDelivery")]
    [InlineData("Shipped")]
    [InlineData("InTransit")]
    [InlineData("ArrivedAtWarehouse")]
    [InlineData("DeliveryFailed")]
    [InlineData("Returned")]
    public async Task SkippingAhead_Returns422_ExplainsWhatIsAllowed_AndChangesNothing(string target)
    {
        var (parcel, customer, branch) = await NewParcelAsync();
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        var warehouse = await _helpers.CreateWarehouseAsync(branch);

        var response = await admin.PostAsJsonAsync(StatusUri(parcel.Id), To(target, "reason", branch));
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Business rule violated", problem.GetProperty("title").GetString());
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains("cannot move to", detail, StringComparison.Ordinal);
        Assert.Contains("Picked up", detail, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, warehouse);

        var unchanged = await ReadAsync<ParcelResponse>(await customer.GetAsync(ParcelUri(parcel.Id)));
        Assert.Equal("BookingCreated", unchanged.Status);
        Assert.Single(unchanged.History);
    }

    [Fact]
    public async Task ATerminalParcel_CannotBeChangedAgain()
    {
        var (parcel, _, _) = await NewParcelAsync();
        await _helpers.AdvanceAsync(
            parcel.Id,
            ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.OutForDelivery, ParcelStatusCode.Delivered);
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);

        foreach (var status in new[] { "PickedUp", "Shipped", "InTransit", "OutForDelivery", "Delivered", "DeliveryFailed", "Returned" })
        {
            var response = await admin.PostAsJsonAsync(StatusUri(parcel.Id), To(status, "reason", parcel.OriginBranch.Id));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        var view = await ReadAsync<ParcelResponse>(await admin.GetAsync(ParcelUri(parcel.Id)));
        Assert.Equal("Delivered", view.Status);
        Assert.Equal(5, view.History.Count);
        Assert.Empty(view.AllowedNextStatuses);
    }

    [Fact]
    public async Task Returned_IsOnlyPossibleAfterAFailedDelivery()
    {
        var (parcel, _, branch) = await NewParcelAsync();
        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.InTransit);
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);

        var tooEarly = await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("Returned", "Sender changed their mind", branch));
        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.OutForDelivery, ParcelStatusCode.DeliveryFailed);
        var afterFailure = await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("Returned", "Receiver refused the parcel", branch));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooEarly.StatusCode);
        Assert.Contains("after a delivery attempt has failed", await tooEarly.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, afterFailure.StatusCode);
    }

    [Fact]
    public async Task AFailedDelivery_CanBeRetried()
    {
        var (parcel, _, branch) = await NewParcelAsync();
        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.OutForDelivery);
        var rider = await _helpers.StaffAsync(Roles.DeliveryAgent);

        var failed = await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("DeliveryFailed", "Nobody at home"));
        var retry = await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("OutForDelivery", branchId: branch));
        var delivered = await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("Delivered", "Left with the neighbour"));

        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
    }

    // ---- required remarks, branches and warehouses -------------------------------------------------------------

    [Fact]
    public async Task DeliveryFailed_WithoutRemarks_IsRefused()
    {
        var (parcel, _, _) = await NewParcelAsync();
        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.OutForDelivery);
        var rider = await _helpers.StaffAsync(Roles.DeliveryAgent);

        var response = await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("DeliveryFailed"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Remarks are required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shipped_NeedsABranch_ArrivedAtWarehouse_NeedsAWarehouse_OutForDelivery_NeedsOneOrTheOther()
    {
        var (parcel, _, branch) = await NewParcelAsync();
        var warehouse = await _helpers.CreateWarehouseAsync(branch);
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("Shipped"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("Shipped", warehouseId: warehouse))).StatusCode);
        (await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("Shipped", branchId: branch))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("ArrivedAtWarehouse"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("ArrivedAtWarehouse", branchId: branch))).StatusCode);
        (await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("ArrivedAtWarehouse", warehouseId: warehouse))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("OutForDelivery"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("OutForDelivery", branchId: branch, warehouseId: warehouse))).StatusCode);
        (await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("OutForDelivery", warehouseId: warehouse))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AnUnknownBranchOrWarehouse_Returns404()
    {
        var (parcel, _, _) = await NewParcelAsync();
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp);

        var branch = await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("Shipped", branchId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, branch.StatusCode);
        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.Shipped);
        var warehouse = await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("ArrivedAtWarehouse", warehouseId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, warehouse.StatusCode);
    }

    // ---- who may do what ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Roles.Customer, "PickedUp")]
    [InlineData(Roles.Accountant, "PickedUp")]
    [InlineData(Roles.WarehouseStaff, "PickedUp")]
    public async Task RolesThatMayNotMakeAChange_Get403_AndTheParcelIsUntouched(string role, string status)
    {
        var (parcel, customer, _) = await NewParcelAsync();
        var client = await _helpers.StaffAsync(role);

        var response = await client.PostAsJsonAsync(StatusUri(parcel.Id), To(status));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", ProblemMediaType(response));
        var view = await ReadAsync<ParcelResponse>(await customer.GetAsync(ParcelUri(parcel.Id)));
        Assert.Equal("BookingCreated", view.Status);
    }

    [Fact]
    public async Task EachRole_IsLimitedToTheChangesItsJobNeeds()
    {
        var (parcel, _, branch) = await NewParcelAsync();
        var warehouse = await _helpers.CreateWarehouseAsync(branch);
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);
        var hub = await _helpers.StaffAsync(Roles.WarehouseStaff);
        var rider = await _helpers.StaffAsync(Roles.DeliveryAgent);
        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped);

        // The counter cannot run the network; the rider cannot move a parcel between hubs.
        Assert.Equal(HttpStatusCode.Forbidden, (await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("InTransit"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("InTransit"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("ArrivedAtWarehouse", warehouseId: warehouse))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hub.PostAsJsonAsync(StatusUri(parcel.Id), To("ArrivedAtWarehouse", warehouseId: warehouse))).StatusCode);

        // Only the rider (or a manager) reports a delivery; the warehouse and the counter cannot.
        Assert.Equal(HttpStatusCode.OK, (await hub.PostAsJsonAsync(StatusUri(parcel.Id), To("OutForDelivery", warehouseId: warehouse))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await hub.PostAsJsonAsync(StatusUri(parcel.Id), To("Delivered"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("Delivered"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("Delivered"))).StatusCode);
    }

    [Fact]
    public async Task WithoutSigningIn_Returns401()
    {
        var (parcel, _, _) = await NewParcelAsync();

        var response = await factory.CreateClient().PostAsJsonAsync(StatusUri(parcel.Id), To("PickedUp"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", ProblemMediaType(response));
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("BookingCreated")]
    [InlineData("Booked")]
    [InlineData("Teleported")]
    [InlineData("")]
    [InlineData("2")]
    public async Task AnUnknownOrUnsettableStatus_Returns400(string status)
    {
        var (parcel, _, _) = await NewParcelAsync();
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);

        var response = await admin.PostAsJsonAsync(StatusUri(parcel.Id), To(status));
        var errors = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("errors");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(errors.TryGetProperty("Status", out _));
    }

    [Fact]
    public async Task AnUnknownParcel_Returns404()
    {
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(StatusUri(Guid.NewGuid()), To("PickedUp"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync(new Uri("/api/v1/parcels/tracking/AFNAAAAAAAAAAA/status", UriKind.Relative), To("PickedUp"))).StatusCode);
    }

    [Fact]
    public async Task TooLongRemarks_Return400()
    {
        var (parcel, _, _) = await NewParcelAsync();
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);

        var response = await admin.PostAsJsonAsync(StatusUri(parcel.Id), To("PickedUp", new string('x', 501)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- what the parcel screen offers -------------------------------------------------------------------------

    [Fact]
    public async Task TheParcelShowsEachCallerOnlyTheChangesTheyMayMake()
    {
        var (parcel, customer, _) = await NewParcelAsync();
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);
        var hub = await _helpers.StaffAsync(Roles.WarehouseStaff);
        var accountant = await _helpers.StaffAsync(Roles.Accountant);
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);

        Assert.Equal(["PickedUp"], (await ReadAsync<ParcelResponse>(await counter.GetAsync(ParcelUri(parcel.Id)))).AllowedNextStatuses);
        Assert.Equal(["PickedUp"], (await ReadAsync<ParcelResponse>(await admin.GetAsync(ParcelUri(parcel.Id)))).AllowedNextStatuses);
        Assert.Empty((await ReadAsync<ParcelResponse>(await hub.GetAsync(ParcelUri(parcel.Id)))).AllowedNextStatuses);
        Assert.Empty((await ReadAsync<ParcelResponse>(await accountant.GetAsync(ParcelUri(parcel.Id)))).AllowedNextStatuses);
        Assert.Empty((await ReadAsync<ParcelResponse>(await customer.GetAsync(ParcelUri(parcel.Id)))).AllowedNextStatuses);

        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.OutForDelivery);
        Assert.Equal(
            ["Delivered", "DeliveryFailed"],
            (await ReadAsync<ParcelResponse>(await admin.GetAsync(ParcelUri(parcel.Id)))).AllowedNextStatuses.Order(StringComparer.Ordinal).ToArray());
        Assert.Empty((await ReadAsync<ParcelResponse>(await counter.GetAsync(ParcelUri(parcel.Id)))).AllowedNextStatuses);
    }

    [Fact]
    public async Task TheParcelList_FiltersByTheNewStatusNames()
    {
        var (parcel, _, _) = await NewParcelAsync();
        var admin = await _helpers.StaffAsync(Roles.SuperAdmin);
        await _helpers.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped);

        var shipped = await ReadAsync<NepalParcel.Contracts.Common.PagedResponse<ParcelSummaryResponse>>(
            await admin.GetAsync(new Uri($"/api/v1/parcels?status=Shipped&search={parcel.TrackingNumber}", UriKind.Relative)));
        var old = await admin.GetAsync(new Uri("/api/v1/parcels?status=ReceivedAtBranch", UriKind.Relative));

        Assert.Equal(parcel.Id, Assert.Single(shipped.Items).Id);
        Assert.Equal("Shipped", shipped.Items[0].Status);
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
    }

    // ---- history can never be rewritten ------------------------------------------------------------------------

    [Fact]
    public async Task History_CannotBeEdited_OrDeleted_EvenByCodeThatTriesToDoSo()
    {
        var (parcel, _, _) = await NewParcelAsync();
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);
        (await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("PickedUp", "Original remark"))).EnsureSuccessStatusCode();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
            var row = await db.ParcelStatusHistory.FirstAsync(h => h.ParcelId == parcel.Id && h.ToStatusId == (int)ParcelStatusCode.PickedUp);
            db.Entry(row).Property(h => h.Remarks).CurrentValue = "Rewritten!";
            db.Entry(row).Property(h => h.ToStatusId).CurrentValue = (int)ParcelStatusCode.Delivered;

            var edit = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("append-only", edit.Message, StringComparison.Ordinal);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
            db.ParcelStatusHistory.Remove(await db.ParcelStatusHistory.FirstAsync(h => h.ParcelId == parcel.Id));

            var delete = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("append-only", delete.Message, StringComparison.Ordinal);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
            var rows = await db.ParcelStatusHistory.AsNoTracking().Where(h => h.ParcelId == parcel.Id).ToListAsync();

            Assert.Equal(2, rows.Count);
            Assert.Equal("Original remark", rows.Single(h => h.ToStatusId == (int)ParcelStatusCode.PickedUp).Remarks);
        }
    }

    [Fact]
    public async Task EveryStatusChange_IsAlsoWrittenToTheAuditTrail()
    {
        var (parcel, _, _) = await NewParcelAsync();
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);

        (await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("PickedUp", "Customer phone 9801112222"))).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "ParcelStatusChanged" && a.EntityId == parcel.Id.ToString());
        Assert.NotNull(audit.UserId);
        Assert.Contains("PickedUp", audit.NewValues, StringComparison.Ordinal);
        Assert.DoesNotContain("9801112222", audit.NewValues, StringComparison.Ordinal);
    }
}
