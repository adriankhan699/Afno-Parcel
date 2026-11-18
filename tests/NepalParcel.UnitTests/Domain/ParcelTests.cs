using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.UnitTests.Domain;

public sealed class ParcelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 6, 0, 0, TimeSpan.Zero);

    private static Parcel NewParcel(decimal cod = 0m) =>
        Parcel.Book(
            TrackingNumbers.Generate(),
            Guid.NewGuid(),
            "Sita Sharma",
            "9841234567",
            TestData.KathmanduAddress(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ServiceType.Standard,
            BookingChannel.Counter,
            weightKg: 2.5m,
            declaredValue: 5000m,
            deliveryCharge: 250.555m,
            codAmount: cod,
            bookedAtUtc: Now,
            bookedByName: "Counter staff");

    [Fact]
    public void Book_StartsAsBooked_WithInitialHistory_AndRoundedMoney()
    {
        var parcel = NewParcel(cod: 5000m);

        Assert.Equal(ParcelStatusCode.Booked, parcel.Status);
        Assert.Single(parcel.StatusHistory);
        Assert.Null(parcel.StatusHistory.Single().FromStatusId);
        Assert.Equal((int)ParcelStatusCode.Booked, parcel.StatusHistory.Single().ToStatusId);
        Assert.Equal(250.56m, parcel.DeliveryCharge);
        Assert.True(parcel.IsCod);
        Assert.Equal("9841234567", parcel.ReceiverPhone);
    }

    [Fact]
    public void Book_InvalidTrackingNumber_Throws()
    {
        Assert.Throws<DomainException>(() => Parcel.Book(
            "AFN0000000000X", Guid.NewGuid(), "A", "9841234567", TestData.KathmanduAddress(), Guid.NewGuid(), null,
            ServiceType.Standard, BookingChannel.Online, 1m, 0m, 0m, 0m, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Book_NonPositiveWeight_Throws(double weight)
    {
        Assert.Throws<DomainException>(() => Parcel.Book(
            TrackingNumbers.Generate(), Guid.NewGuid(), "A", "9841234567", TestData.KathmanduAddress(), Guid.NewGuid(), null,
            ServiceType.Standard, BookingChannel.Online, (decimal)weight, 0m, 0m, 0m, Now));
    }

    [Fact]
    public void ChangeStatus_FollowsWorkflow_AndAppendsHistory()
    {
        var parcel = NewParcel();
        var staffId = Guid.NewGuid();
        var warehouseId = Guid.NewGuid();

        parcel.ChangeStatus(ParcelStatusCode.ReceivedAtBranch, Now.AddHours(1), staffId, "Counter staff");
        parcel.ChangeStatus(ParcelStatusCode.InTransit, Now.AddHours(2), staffId, "Counter staff");
        parcel.ChangeStatus(ParcelStatusCode.ReceivedAtWarehouse, Now.AddHours(8), staffId, "Hub staff", "Sorted", warehouseId: warehouseId);
        parcel.ChangeStatus(ParcelStatusCode.OutForDelivery, Now.AddHours(20), staffId, "Hub staff");
        parcel.ChangeStatus(ParcelStatusCode.Delivered, Now.AddHours(24), staffId, "Rider");

        Assert.Equal(ParcelStatusCode.Delivered, parcel.Status);
        Assert.Equal(Now.AddHours(24), parcel.DeliveredAtUtc);
        Assert.Equal(6, parcel.StatusHistory.Count);
        Assert.Equal(
            [1, 3, 4, 5, 6, 7],
            parcel.StatusHistory.OrderBy(h => h.OccurredAtUtc).Select(h => h.ToStatusId).ToArray());
        Assert.Equal(warehouseId, parcel.StatusHistory.Single(h => h.ToStatusId == (int)ParcelStatusCode.ReceivedAtWarehouse).WarehouseId);
    }

    [Theory]
    [InlineData(ParcelStatusCode.Delivered)]
    [InlineData(ParcelStatusCode.OutForDelivery)]
    [InlineData(ParcelStatusCode.Booked)]
    public void ChangeStatus_IllegalJump_Throws_AndKeepsHistoryUnchanged(ParcelStatusCode target)
    {
        var parcel = NewParcel();

        Assert.Throws<DomainException>(() => parcel.ChangeStatus(target, Now, null, null));

        Assert.Equal(ParcelStatusCode.Booked, parcel.Status);
        Assert.Single(parcel.StatusHistory);
    }

    [Theory]
    [InlineData(ParcelStatusCode.Delivered)]
    [InlineData(ParcelStatusCode.Returned)]
    [InlineData(ParcelStatusCode.Cancelled)]
    public void TerminalStatuses_AllowNoFurtherChanges(ParcelStatusCode terminal)
    {
        Assert.Empty(ParcelStatusWorkflow.NextStatuses(terminal));
        Assert.True(ParcelStatusCatalog.All.Single(s => s.Code == terminal).IsTerminal);
    }

    [Fact]
    public void FailedDelivery_CanBeRetried_OrReturned()
    {
        var parcel = NewParcel();
        foreach (var next in new[]
                 {
                     ParcelStatusCode.ReceivedAtBranch, ParcelStatusCode.OutForDelivery, ParcelStatusCode.DeliveryFailed,
                     ParcelStatusCode.OutForDelivery, ParcelStatusCode.DeliveryFailed, ParcelStatusCode.ReturnToSender,
                     ParcelStatusCode.Returned,
                 })
        {
            parcel.ChangeStatus(next, Now, null, null);
        }

        Assert.Equal(ParcelStatusCode.Returned, parcel.Status);
        Assert.Equal(8, parcel.StatusHistory.Count);
    }

    [Fact]
    public void AddItem_OnlyWhileBooked_AndValidatesInput()
    {
        var parcel = NewParcel();

        var item = parcel.AddItem("Books", 3, 0.5m, 400m, "Stationery", isFragile: false);
        Assert.Equal(3, item.Quantity);
        Assert.Throws<DomainException>(() => parcel.AddItem("Bad", 0, 1m, 1m));

        parcel.ChangeStatus(ParcelStatusCode.ReceivedAtBranch, Now, null, null);
        Assert.Throws<DomainException>(() => parcel.AddItem("Late", 1, 1m, 1m));
        Assert.Single(parcel.Items);
    }

    [Fact]
    public void StatusCatalog_HasAllCodes_WithStableIds()
    {
        var ids = ParcelStatusCatalog.All.Select(s => (int)s.Code).ToArray();

        Assert.Equal(Enum.GetValues<ParcelStatusCode>().Length, ids.Length);
        Assert.Equal(Enumerable.Range(1, 11), ids);
    }
}
