using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.IntegrationTests.Infrastructure;

namespace NepalParcel.IntegrationTests;

/// <summary>Verifies the EF Core model: seed data, mappings, history preservation and uniqueness rules.</summary>
public sealed class PersistenceTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Seed_ContainsTheSevenRoles_AndAllParcelStatuses()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();

        var roles = await db.Roles.Select(r => r.Name!).ToListAsync();
        var statuses = await db.ParcelStatuses.OrderBy(s => s.Id).ToListAsync();

        Assert.Equal(Roles.All.Order(StringComparer.Ordinal), roles.Order(StringComparer.Ordinal));
        Assert.Equal(11, statuses.Count);
        Assert.Equal("Booked", statuses[0].Code);
        Assert.Equal(["Delivered", "Returned", "Cancelled"], statuses.Where(s => s.IsTerminal).Select(s => s.Code).ToArray());
    }

    [Fact]
    public async Task Parcel_RoundTrips_WithItemsHistoryAndAddress()
    {
        var (parcelId, tracking) = await SaveParcelAsync(statusChanges: 2);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var parcel = await db.Parcels.Include(p => p.Items).Include(p => p.StatusHistory).SingleAsync(p => p.Id == parcelId);

        Assert.Equal(tracking, parcel.TrackingNumber);
        Assert.Equal(ParcelStatusCode.InTransit, parcel.Status);
        Assert.Equal(2, parcel.Items.Count);
        Assert.Equal(3, parcel.StatusHistory.Count);
        Assert.Equal("Kathmandu", parcel.ReceiverAddress.District);
        Assert.Equal(1500.50m, parcel.DeliveryCharge);
        Assert.Equal(2.250m, parcel.WeightKg);
        Assert.Equal("anonymous", parcel.CreatedBy);
        Assert.NotEqual(default, parcel.CreatedAtUtc);
    }

    [Fact]
    public async Task TrackingNumber_MustBeUnique()
    {
        var (_, tracking) = await SaveParcelAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var existing = await db.Parcels.AsNoTracking().SingleAsync(p => p.TrackingNumber == tracking);
        var duplicate = NewParcel(tracking, existing.SenderCustomerId, existing.OriginBranchId);
        db.Parcels.Add(duplicate);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task StatusHistory_IsPreserved_ParentsCannotBeDeleted()
    {
        var (parcelId, _) = await SaveParcelAsync(statusChanges: 1);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var parcel = await db.Parcels.SingleAsync(p => p.Id == parcelId);
        db.Parcels.Remove(parcel);

        // Foreign keys are restrictive and history rows exist, so the parcel cannot be hard-deleted.
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Customer_WalkIn_AndOnline_RoundTrip_WithOptionalAddress()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var walkIn = Customer.Create("Walk In", "9841000001");
        var withAddress = Customer.Create("With Address", "9841000002", address: TestAddress());
        db.Customers.AddRange(walkIn, withAddress);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var loadedWalkIn = await db.Customers.SingleAsync(c => c.Id == walkIn.Id);
        var loadedWithAddress = await db.Customers.SingleAsync(c => c.Id == withAddress.Id);

        Assert.Null(loadedWalkIn.Address);
        Assert.True(loadedWalkIn.IsWalkIn);
        Assert.Equal("Pokhara Metropolitan City", loadedWithAddress.Address!.Municipality);
    }

    [Fact]
    public async Task Enums_AreStoredAsReadableText()
    {
        var (parcelId, _) = await SaveParcelAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        // SQLite has no schemas, so the table is unqualified here (SQL Server uses parcel.Parcels).
        var raw = await db.Database
            .SqlQuery<string>($"SELECT ServiceType AS Value FROM Parcels WHERE Id = {parcelId}")
            .SingleAsync();

        Assert.Equal("Express", raw);
    }

    private async Task<(Guid ParcelId, string Tracking)> SaveParcelAsync(int statusChanges = 0)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();

        var branch = Branch.Create("P" + Guid.NewGuid().ToString("N")[..8], "Test Branch", BranchType.Branch, TestAddress(), "9841000000");
        var customer = Customer.Create("Sender Test", "9841000003");
        db.AddRange(branch, customer);
        await db.SaveChangesAsync();

        var tracking = TrackingNumbers.Generate();
        var parcel = NewParcel(tracking, customer.Id, branch.Id);
        parcel.AddItem("Books", 2, 0.5m, 400m, "Stationery");
        parcel.AddItem("Clothes", 1, 1.25m, 2500m, isFragile: false);
        var steps = new[] { ParcelStatusCode.ReceivedAtBranch, ParcelStatusCode.InTransit };
        foreach (var step in steps.Take(statusChanges))
        {
            parcel.ChangeStatus(step, Now.AddHours(1), null, "Staff");
        }

        db.Parcels.Add(parcel);
        await db.SaveChangesAsync();
        return (parcel.Id, tracking);
    }

    private static Parcel NewParcel(string tracking, Guid customerId, Guid branchId) =>
        Parcel.Book(
            tracking, customerId, "Receiver Name", "9851000000", NepalAddress.Create(Province.Bagmati, "Kathmandu", "Kathmandu Metropolitan City", 5),
            branchId, null, ServiceType.Express, BookingChannel.Online, 2.25m, 3300m, 1500.50m, 0m, Now);

    private static NepalAddress TestAddress() =>
        NepalAddress.Create(Province.Gandaki, "Kaski", "Pokhara Metropolitan City", 8, "Lakeside");
}
