using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;
using NepalParcel.Domain.Pricing;
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
        Assert.Equal(10, statuses.Count);
        Assert.Equal("BookingCreated", statuses[0].Code);
        Assert.Equal(["Delivered", "Returned", "Cancelled"], statuses.Where(s => s.IsTerminal).Select(s => s.Code).ToArray());
    }

    [Fact]
    public async Task Parcel_RoundTrips_WithItemsHistoryAndAddress()
    {
        var (parcelId, tracking) = await SaveParcelAsync(statusChanges: 3);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var parcel = await db.Parcels.Include(p => p.Items).Include(p => p.StatusHistory).SingleAsync(p => p.Id == parcelId);

        Assert.Equal(tracking, parcel.TrackingNumber);
        Assert.Equal(ParcelStatusCode.InTransit, parcel.Status);
        Assert.Equal(2, parcel.Items.Count);
        Assert.Equal(4, parcel.StatusHistory.Count);
        Assert.Equal("Kathmandu", parcel.ReceiverAddress.District);
        Assert.Equal(1469m, parcel.DeliveryCharge);
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
        var steps = new[] { ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.InTransit };
        foreach (var step in steps.Take(statusChanges))
        {
            parcel.ChangeStatus(step, Now.AddHours(1), null, "Staff", branchId: branch.Id);
        }

        db.Parcels.Add(parcel);
        await db.SaveChangesAsync();
        return (parcel.Id, tracking);
    }

    private static Parcel NewParcel(string tracking, Guid customerId, Guid branchId) =>
        Parcel.Book(
            new NewParcelDetails
            {
                TrackingNumber = tracking,
                SenderCustomerId = customerId,
                SenderName = "Sender Test",
                SenderPhone = "9841000003",
                SenderAddress = TestAddress(),
                ReceiverName = "Receiver Name",
                ReceiverPhone = "9851000000",
                ReceiverAddress = NepalAddress.Create(Province.Bagmati, "Kathmandu", "Kathmandu Metropolitan City", 5),
                OriginBranchId = branchId,
                ServiceType = ServiceType.Express,
                Channel = BookingChannel.Online,
                Description = "Books and clothes",
                Quantity = 3,
                WeightKg = 2.25m,
                DeclaredValue = 3300m,
                PaymentMethod = PaymentMethod.Cash,
                Price = new PriceBreakdown(2.25m, 0m, 2.5m, 1000m, 300m, 1300m, 0m, 13m, 169m, 1469m),
            },
            Now);

    private static NepalAddress TestAddress() =>
        NepalAddress.Create(Province.Gandaki, "Kaski", "Pokhara Metropolitan City", 8, "Lakeside");
}
