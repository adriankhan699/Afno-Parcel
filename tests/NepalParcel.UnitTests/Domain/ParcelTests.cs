using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;
using NepalParcel.Domain.Pricing;

namespace NepalParcel.UnitTests.Domain;

public sealed class ParcelTests
{
    private static readonly DateTimeOffset Now = TestData.Now;

    private static Parcel NewParcel(decimal cod = 0m) => TestData.NewParcel(codGoods: cod);

    /// <summary>Walks a new parcel along the happy path as far as <paramref name="upTo"/>.</summary>
    internal static Parcel ParcelAt(ParcelStatusCode upTo)
    {
        var parcel = NewParcel();
        var branch = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var t = Now;
        foreach (var step in new[]
                 {
                     ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.InTransit,
                     ParcelStatusCode.ArrivedAtWarehouse, ParcelStatusCode.OutForDelivery, ParcelStatusCode.Delivered,
                 })
        {
            if (parcel.Status == upTo)
            {
                break;
            }

            t = t.AddHours(1);
            parcel.ChangeStatus(
                step,
                t,
                Guid.NewGuid(),
                "Staff",
                branchId: step is ParcelStatusCode.Shipped or ParcelStatusCode.OutForDelivery ? branch : null,
                warehouseId: step == ParcelStatusCode.ArrivedAtWarehouse ? warehouse : null);
        }

        return parcel;
    }

    [Fact]
    public void Book_StartsAsBookingCreated_WithInitialHistory_AndStoresThePassedPrice()
    {
        var details = TestData.ParcelDetails(codGoods: 5000m);
        var parcel = Parcel.Book(details, Now, bookedByName: "Counter staff");

        Assert.Equal(ParcelStatusCode.BookingCreated, parcel.Status);
        Assert.Single(parcel.StatusHistory);
        Assert.Null(parcel.StatusHistory.Single().FromStatusId);
        Assert.Equal((int)ParcelStatusCode.BookingCreated, parcel.StatusHistory.Single().ToStatusId);
        Assert.Equal(details.Price.TotalCharge, parcel.DeliveryCharge);
        Assert.Equal(details.Price.ShippingCharge, parcel.ShippingCharge);
        Assert.Equal(details.Price.CodFee, parcel.CodFee);
        Assert.Equal(details.Price, parcel.Price);
        Assert.True(parcel.IsCod);
        Assert.Equal("9851234567", parcel.ReceiverPhone);
        Assert.Equal("Ram Sharma", parcel.SenderName);
        Assert.Equal("Blue gate", parcel.ReceiverAddressInstructions);
        Assert.Equal(details.OriginBranchId, parcel.CurrentBranchId);
    }

    [Fact]
    public void Book_PrepaidWithoutGoodsAmount_IsNotCod()
    {
        var parcel = TestData.NewParcel(PaymentMethod.Cash);

        Assert.False(parcel.IsCod);
        Assert.Equal(0m, parcel.CodAmount);
    }

    [Fact]
    public void Book_PrepaidWithGoodsAmount_CollectsOnlyTheGoodsAmount()
    {
        var parcel = TestData.NewParcel(PaymentMethod.Esewa, codGoods: 4000m);

        Assert.Equal(4000m, parcel.CodAmount);
        Assert.True(parcel.IsCod);
    }

    [Fact]
    public void Book_CashOnDelivery_ReceiverAlsoPaysTheDeliveryCharge()
    {
        var withoutGoods = TestData.NewParcel(PaymentMethod.CashOnDelivery);
        var withGoods = TestData.NewParcel(PaymentMethod.CashOnDelivery, codGoods: 4000m);

        Assert.Equal(withoutGoods.DeliveryCharge, withoutGoods.CodAmount);
        Assert.True(withoutGoods.IsCod);
        Assert.Equal(4000m + withGoods.DeliveryCharge, withGoods.CodAmount);
    }

    [Fact]
    public void Book_InvalidTrackingNumber_Throws()
    {
        var details = TestData.ParcelDetails(trackingNumber: "AFN0000000000X");

        Assert.Throws<DomainException>(() => Parcel.Book(details, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Book_NonPositiveWeight_Throws(double weight)
    {
        var details = TestData.ParcelDetails() with { WeightKg = (decimal)weight };

        Assert.Throws<DomainException>(() => Parcel.Book(details, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Book_QuantityOutsideLimits_Throws(int quantity)
    {
        var details = TestData.ParcelDetails() with { Quantity = quantity };

        Assert.Throws<DomainException>(() => Parcel.Book(details, Now));
    }

    [Fact]
    public void Book_PartialDimensions_Throws()
    {
        var details = TestData.ParcelDetails() with { LengthCm = 30m };

        Assert.Throws<DomainException>(() => Parcel.Book(details, Now));
    }

    [Fact]
    public void Book_WithDimensions_StoresVolumetricWeight()
    {
        // 50 × 40 × 30 cm = 60 000 cm³ ÷ 5000 = 12 kg volumetric, so the price must be based on at least 12 kg.
        var rule = TestData.Rule(minKg: 5m, maxKg: 20m);
        var price = PricingCalculator.Calculate(rule, 2.5m, 12m, 12m, 0m, 13m);
        var details = TestData.ParcelDetails(price: price) with { LengthCm = 50m, WidthCm = 40m, HeightCm = 30m };

        var parcel = Parcel.Book(details, Now);

        Assert.Equal(12m, parcel.VolumetricWeightKg);
        Assert.Equal(12m, parcel.ChargeableWeightKg);
        Assert.Equal(2.5m, parcel.WeightKg);
    }

    [Fact]
    public void Book_ChargeableWeightBelowActualWeight_Throws()
    {
        var price = PricingCalculator.Calculate(TestData.Rule(minKg: 0m, maxKg: 5m), 1m, 0m, 1m, 0m, 13m);
        var details = TestData.ParcelDetails(price: price);

        Assert.Throws<DomainException>(() => Parcel.Book(details, Now));
    }

    [Fact]
    public void Book_PriceThatDoesNotAddUp_Throws()
    {
        var tampered = TestData.Price() with { TotalCharge = 1m };

        Assert.Throws<DomainException>(() => Parcel.Book(TestData.ParcelDetails(price: tampered), Now));
    }

    [Fact]
    public void ChangeStatus_FollowsTheWorkflow_AndAppendsAFullHistoryRowEachTime()
    {
        var parcel = NewParcel();
        var staffId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var warehouseId = Guid.NewGuid();

        parcel.ChangeStatus(ParcelStatusCode.PickedUp, Now.AddHours(1), staffId, "Counter staff");
        parcel.ChangeStatus(ParcelStatusCode.Shipped, Now.AddHours(2), staffId, "Counter staff", "Loaded on the 6am truck", branchId: branchId);
        parcel.ChangeStatus(ParcelStatusCode.InTransit, Now.AddHours(3), staffId, "Driver");
        parcel.ChangeStatus(ParcelStatusCode.ArrivedAtWarehouse, Now.AddHours(8), staffId, "Hub staff", "Sorted", warehouseId: warehouseId);
        parcel.ChangeStatus(ParcelStatusCode.OutForDelivery, Now.AddHours(20), staffId, "Hub staff", branchId: branchId);
        parcel.ChangeStatus(ParcelStatusCode.Delivered, Now.AddHours(24), staffId, "Rider");

        Assert.Equal(ParcelStatusCode.Delivered, parcel.Status);
        Assert.Equal(Now.AddHours(24), parcel.DeliveredAtUtc);
        var history = parcel.StatusHistory.OrderBy(h => h.OccurredAtUtc).ToList();
        Assert.Equal(7, history.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], history.Select(h => h.ToStatusId).ToArray());
        Assert.Equal([null, 1, 2, 3, 4, 5, 6], history.Select(h => h.FromStatusId).ToArray());

        // Each row records who, where, when and why.
        var shipped = history.Single(h => h.ToStatusId == (int)ParcelStatusCode.Shipped);
        Assert.Equal(staffId, shipped.ChangedByUserId);
        Assert.Equal(branchId, shipped.BranchId);
        Assert.Equal(Now.AddHours(2), shipped.OccurredAtUtc);
        Assert.Equal("Loaded on the 6am truck", shipped.Remarks);
        Assert.Equal(warehouseId, history.Single(h => h.ToStatusId == (int)ParcelStatusCode.ArrivedAtWarehouse).WarehouseId);
    }

    [Theory]
    [InlineData(ParcelStatusCode.Delivered)]
    [InlineData(ParcelStatusCode.OutForDelivery)]
    [InlineData(ParcelStatusCode.Shipped)]
    [InlineData(ParcelStatusCode.InTransit)]
    [InlineData(ParcelStatusCode.BookingCreated)]
    public void ChangeStatus_IllegalJump_Throws_AndKeepsHistoryUnchanged(ParcelStatusCode target)
    {
        var parcel = NewParcel();

        var ex = Assert.Throws<DomainException>(() => parcel.ChangeStatus(target, Now, null, null, "reason", Guid.NewGuid()));

        Assert.Contains("cannot move to", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ParcelStatusCode.BookingCreated, parcel.Status);
        Assert.Single(parcel.StatusHistory);
    }

    [Fact]
    public void ChangeStatus_IllegalMove_ExplainsWhatIsAllowed()
    {
        var parcel = NewParcel();

        var ex = Assert.Throws<DomainException>(() => parcel.ChangeStatus(ParcelStatusCode.Delivered, Now, null, null));

        Assert.Contains("Booking created", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Picked up", ex.Message, StringComparison.Ordinal);
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
    public void ADeliveredParcel_CanNeverBeChangedAgain()
    {
        var parcel = ParcelAt(ParcelStatusCode.Delivered);

        foreach (var status in Enum.GetValues<ParcelStatusCode>())
        {
            Assert.Throws<DomainException>(() => parcel.ChangeStatus(status, Now.AddDays(5), null, null, "any reason", Guid.NewGuid()));
        }

        Assert.Equal(ParcelStatusCode.Delivered, parcel.Status);
        Assert.Empty(parcel.AllowedNextStatuses());
    }

    [Fact]
    public void FailedDelivery_CanBeRetried_BroughtBack_OrReturned()
    {
        var parcel = ParcelAt(ParcelStatusCode.OutForDelivery);
        var t = Now.AddDays(1);
        var branch = Guid.NewGuid();

        parcel.ChangeStatus(ParcelStatusCode.DeliveryFailed, t, null, null, "Receiver not home");
        parcel.ChangeStatus(ParcelStatusCode.OutForDelivery, t.AddHours(1), null, null, branchId: branch);
        parcel.ChangeStatus(ParcelStatusCode.DeliveryFailed, t.AddHours(2), null, null, "Phone switched off");
        parcel.ChangeStatus(ParcelStatusCode.Returned, t.AddHours(3), null, null, "Sender asked for it back", branch);

        Assert.Equal(ParcelStatusCode.Returned, parcel.Status);
        Assert.Equal(6 + 4, parcel.StatusHistory.Count); // the journey so far plus four more steps
    }

    [Fact]
    public void Returned_IsOnlyPossibleAfterADeliveryAttemptFailed()
    {
        var parcel = ParcelAt(ParcelStatusCode.ArrivedAtWarehouse);

        Assert.DoesNotContain(ParcelStatusCode.Returned, parcel.AllowedNextStatuses());
        var ex = Assert.Throws<DomainException>(() =>
            parcel.ChangeStatus(ParcelStatusCode.Returned, Now.AddDays(1), null, null, "Changed my mind", Guid.NewGuid()));
        Assert.Contains("after a delivery attempt has failed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ParcelStatusCode.ArrivedAtWarehouse, parcel.Status);
    }

    [Fact]
    public void AfterAFailedDelivery_TheParcelCanTravelBackThroughTheNetworkAndBeReturned()
    {
        var parcel = ParcelAt(ParcelStatusCode.OutForDelivery);
        var t = Now.AddDays(1);
        parcel.ChangeStatus(ParcelStatusCode.DeliveryFailed, t, null, null, "Address not found");

        Assert.Contains(ParcelStatusCode.Returned, parcel.AllowedNextStatuses());
        parcel.ChangeStatus(ParcelStatusCode.InTransit, t.AddHours(1), null, null);
        parcel.ChangeStatus(ParcelStatusCode.ArrivedAtWarehouse, t.AddHours(2), null, null, warehouseId: Guid.NewGuid());
        parcel.ChangeStatus(ParcelStatusCode.Returned, t.AddHours(3), null, null, "Handed back to the sender", Guid.NewGuid());

        Assert.Equal(ParcelStatusCode.Returned, parcel.Status);
    }

    [Theory]
    [InlineData(ParcelStatusCode.OutForDelivery)]
    public void DeliveryFailed_NeedsRemarks_AndLeavesTheParcelUntouchedWhenMissing(ParcelStatusCode from)
    {
        var parcel = ParcelAt(from);

        Assert.Throws<DomainException>(() => parcel.ChangeStatus(ParcelStatusCode.DeliveryFailed, Now.AddDays(1), null, null));
        Assert.Throws<DomainException>(() => parcel.ChangeStatus(ParcelStatusCode.DeliveryFailed, Now.AddDays(1), null, null, "   "));

        Assert.Equal(from, parcel.Status);
        Assert.False(parcel.HasFailedDelivery);
    }

    [Fact]
    public void Location_IsEnforcedWhereTheStatusNeedsIt()
    {
        var picked = ParcelAt(ParcelStatusCode.PickedUp);
        Assert.Throws<DomainException>(() => picked.ChangeStatus(ParcelStatusCode.Shipped, Now.AddDays(1), null, null)); // needs a branch
        Assert.Throws<DomainException>(() => picked.ChangeStatus(ParcelStatusCode.Shipped, Now.AddDays(1), null, null, warehouseId: Guid.NewGuid())); // a warehouse is not a branch
        Assert.Equal(ParcelStatusCode.PickedUp, picked.Status);

        var transit = ParcelAt(ParcelStatusCode.InTransit);
        Assert.Throws<DomainException>(() => transit.ChangeStatus(ParcelStatusCode.ArrivedAtWarehouse, Now.AddDays(1), null, null)); // needs a warehouse
        Assert.Throws<DomainException>(() => transit.ChangeStatus(ParcelStatusCode.ArrivedAtWarehouse, Now.AddDays(1), null, null, branchId: Guid.NewGuid()));
        Assert.Throws<DomainException>(() => transit.ChangeStatus(ParcelStatusCode.OutForDelivery, Now.AddDays(1), null, null)); // branch or warehouse
        Assert.Throws<DomainException>(() => transit.ChangeStatus(ParcelStatusCode.OutForDelivery, Now.AddDays(1), null, null, branchId: Guid.NewGuid(), warehouseId: Guid.NewGuid())); // not both
        Assert.Equal(ParcelStatusCode.InTransit, transit.Status);
    }

    [Fact]
    public void ChangeStatus_CannotBeBackdatedBeforeThePreviousStep()
    {
        var parcel = NewParcel();
        parcel.ChangeStatus(ParcelStatusCode.PickedUp, Now.AddHours(5), null, null);

        var ex = Assert.Throws<DomainException>(() => parcel.ChangeStatus(ParcelStatusCode.Shipped, Now.AddHours(1), null, null, branchId: Guid.NewGuid()));

        Assert.Contains("earlier than the previous", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ParcelStatusCode.PickedUp, parcel.Status);
    }

    [Fact]
    public void WhereTheParcelIs_FollowsTheLatestBranchOrWarehouse_AndClearsWhileOnTheRoad()
    {
        var parcel = NewParcel();
        var originBranch = parcel.CurrentBranchId;
        var warehouse = Guid.NewGuid();

        parcel.ChangeStatus(ParcelStatusCode.PickedUp, Now.AddHours(1), null, null);
        Assert.Equal(originBranch, parcel.CurrentBranchId);

        parcel.ChangeStatus(ParcelStatusCode.Shipped, Now.AddHours(2), null, null, branchId: originBranch);
        parcel.ChangeStatus(ParcelStatusCode.InTransit, Now.AddHours(3), null, null);
        Assert.Null(parcel.CurrentBranchId);
        Assert.Null(parcel.CurrentWarehouseId);

        parcel.ChangeStatus(ParcelStatusCode.ArrivedAtWarehouse, Now.AddHours(4), null, null, warehouseId: warehouse);
        Assert.Equal(warehouse, parcel.CurrentWarehouseId);
        Assert.Null(parcel.CurrentBranchId);
    }

    [Fact]
    public void AllowedNextStatuses_FollowTheWorkflow()
    {
        Assert.Equal(
            [ParcelStatusCode.PickedUp, ParcelStatusCode.Cancelled],
            NewParcel().AllowedNextStatuses().OrderBy(s => (int)s).ToArray());
        Assert.Equal(
            [ParcelStatusCode.Delivered, ParcelStatusCode.DeliveryFailed],
            ParcelAt(ParcelStatusCode.OutForDelivery).AllowedNextStatuses().OrderBy(s => (int)s).ToArray());
    }

    [Fact]
    public void AddItem_OnlyBeforePickup_AndValidatesInput()
    {
        var parcel = NewParcel();

        var item = parcel.AddItem("Books", 3, 0.5m, 400m, "Stationery", isFragile: false);
        Assert.Equal(3, item.Quantity);
        Assert.Throws<DomainException>(() => parcel.AddItem("Bad", 0, 1m, 1m));

        parcel.ChangeStatus(ParcelStatusCode.PickedUp, Now, null, null);
        Assert.Throws<DomainException>(() => parcel.AddItem("Late", 1, 1m, 1m));
        Assert.Single(parcel.Items);
    }

    [Fact]
    public void StatusCatalog_HasTheTenStatuses_WithStableIds()
    {
        var statuses = ParcelStatusCatalog.All.Select(s => s.Code.ToString()).ToArray();
        var ids = ParcelStatusCatalog.All.Select(s => (int)s.Code).ToArray();

        Assert.Equal(
            ["BookingCreated", "PickedUp", "Shipped", "InTransit", "ArrivedAtWarehouse", "OutForDelivery", "Delivered", "DeliveryFailed", "Returned", "Cancelled"],
            statuses);
        Assert.Equal(Enum.GetValues<ParcelStatusCode>().Length, ids.Length);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 10, 11], ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }
}
