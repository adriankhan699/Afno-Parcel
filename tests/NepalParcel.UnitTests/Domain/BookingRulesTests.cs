using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Pricing;

namespace NepalParcel.UnitTests.Domain;

public sealed class BookingRulesTests
{
    private static readonly DateTimeOffset Now = TestData.Now;

    // ---- cancellation ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ParcelStatusCode.BookingCreated, false, true)]
    [InlineData(ParcelStatusCode.PickedUp, false, false)]
    [InlineData(ParcelStatusCode.Shipped, false, false)]
    [InlineData(ParcelStatusCode.BookingCreated, true, true)]
    [InlineData(ParcelStatusCode.PickedUp, true, true)]
    [InlineData(ParcelStatusCode.Shipped, true, false)]
    [InlineData(ParcelStatusCode.InTransit, true, false)]
    [InlineData(ParcelStatusCode.ArrivedAtWarehouse, true, false)]
    [InlineData(ParcelStatusCode.OutForDelivery, true, false)]
    [InlineData(ParcelStatusCode.Delivered, true, false)]
    [InlineData(ParcelStatusCode.DeliveryFailed, true, false)]
    [InlineData(ParcelStatusCode.Returned, true, false)]
    [InlineData(ParcelStatusCode.Cancelled, true, false)]
    [InlineData(ParcelStatusCode.Cancelled, false, false)]
    public void CancellationPolicy_AllowsCustomersBeforePickup_AndStaffUntilTheParcelLeavesTheBranch(
        ParcelStatusCode status,
        bool byStaff,
        bool expected) =>
        Assert.Equal(expected, CancellationPolicy.CanCancel(status, byStaff));

    [Fact]
    public void CancellationPolicy_NeverAllowsMoreThanTheStatusWorkflow()
    {
        foreach (var status in Enum.GetValues<ParcelStatusCode>())
        {
            if (CancellationPolicy.CanCancel(status, byStaff: true))
            {
                Assert.True(ParcelStatusWorkflow.CanTransition(status, ParcelStatusCode.Cancelled), $"{status} to Cancelled must be a legal transition");
            }
        }
    }

    [Fact]
    public void Cancel_ByCustomer_MovesToCancelled_AndRecordsWhoAndWhy()
    {
        var parcel = TestData.NewParcel();
        var userId = Guid.NewGuid();

        parcel.Cancel("  Changed my mind  ", byStaff: false, Now.AddHours(1), userId, "Ram");

        Assert.Equal(ParcelStatusCode.Cancelled, parcel.Status);
        var last = parcel.StatusHistory.OrderBy(h => h.OccurredAtUtc).Last();
        Assert.Equal((int)ParcelStatusCode.Cancelled, last.ToStatusId);
        Assert.Equal(userId, last.ChangedByUserId);
        Assert.Contains("customer", last.Remarks, StringComparison.Ordinal);
        Assert.Contains("Changed my mind", last.Remarks, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancel_ByCustomer_AfterPickup_IsRefused_AndKeepsTheStatus()
    {
        var parcel = TestData.NewParcel();
        parcel.ChangeStatus(ParcelStatusCode.PickedUp, Now, null, null);

        var ex = Assert.Throws<DomainException>(() => parcel.Cancel("No longer needed", byStaff: false, Now, null, null));

        Assert.Contains("before it is picked up", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ParcelStatusCode.PickedUp, parcel.Status);
    }

    [Fact]
    public void Cancel_ByStaff_AfterPickup_IsAllowed()
    {
        var pickedUp = TestData.NewParcel();
        pickedUp.ChangeStatus(ParcelStatusCode.PickedUp, Now, null, null);

        pickedUp.Cancel("Customer asked at the counter", byStaff: true, Now, null, null);

        Assert.Equal(ParcelStatusCode.Cancelled, pickedUp.Status);
    }

    [Fact]
    public void Cancel_ByStaff_OnceShippedOrInTransit_IsRefused()
    {
        var shipped = TestData.NewParcel();
        shipped.ChangeStatus(ParcelStatusCode.PickedUp, Now, null, null);
        shipped.ChangeStatus(ParcelStatusCode.Shipped, Now, null, null, branchId: Guid.NewGuid());
        var inTransit = TestData.NewParcel();
        inTransit.ChangeStatus(ParcelStatusCode.PickedUp, Now, null, null);
        inTransit.ChangeStatus(ParcelStatusCode.Shipped, Now, null, null, branchId: Guid.NewGuid());
        inTransit.ChangeStatus(ParcelStatusCode.InTransit, Now, null, null);

        Assert.Throws<DomainException>(() => shipped.Cancel("Too late really", byStaff: true, Now, null, null));
        Assert.Throws<DomainException>(() => inTransit.Cancel("Too late really", byStaff: true, Now, null, null));
        Assert.Equal(ParcelStatusCode.Shipped, shipped.Status);
        Assert.Equal(ParcelStatusCode.InTransit, inTransit.Status);
    }

    [Fact]
    public void Cancel_Twice_SaysItIsAlreadyCancelled()
    {
        var parcel = TestData.NewParcel();
        parcel.Cancel("First time", byStaff: false, Now, null, null);

        var ex = Assert.Throws<DomainException>(() => parcel.Cancel("Second time", byStaff: false, Now, null, null));

        Assert.Contains("already cancelled", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void Cancel_WithoutAProperReason_Throws(string reason)
    {
        var parcel = TestData.NewParcel();

        Assert.Throws<DomainException>(() => parcel.Cancel(reason, byStaff: false, Now, null, null));
        Assert.Equal(ParcelStatusCode.BookingCreated, parcel.Status);
    }

    [Fact]
    public void CanBeCancelled_ReflectsThePolicy()
    {
        var parcel = TestData.NewParcel();
        parcel.ChangeStatus(ParcelStatusCode.PickedUp, Now, null, null);

        Assert.False(parcel.CanBeCancelled(byStaff: false));
        Assert.True(parcel.CanBeCancelled(byStaff: true));
    }

    // ---- delivery estimate -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ServiceType.Standard, 0, 1)]
    [InlineData(ServiceType.Standard, 1, 1)]
    [InlineData(ServiceType.Standard, 4, 4)]
    [InlineData(ServiceType.Express, 1, 1)]
    [InlineData(ServiceType.Express, 3, 2)]
    [InlineData(ServiceType.Express, 7, 4)]
    [InlineData(ServiceType.SameDay, 5, 0)]
    public void DeliveryEstimator_DaysDependOnTheServiceType(ServiceType service, int areaDays, int expected) =>
        Assert.Equal(expected, DeliveryEstimator.EstimatedDays(service, areaDays));

    [Fact]
    public void DeliveryEstimator_SameDayIsAFewHours_OthersAreWholeDays()
    {
        Assert.Equal(Now.AddHours(DeliveryEstimator.SameDayHours), DeliveryEstimator.Estimate(Now, ServiceType.SameDay, 3));
        Assert.Equal(Now.AddDays(3), DeliveryEstimator.Estimate(Now, ServiceType.Standard, 3));
        Assert.Equal(Now.AddDays(2), DeliveryEstimator.Estimate(Now, ServiceType.Express, 3));
    }

    // ---- service areas and districts ---------------------------------------------------------------------------

    [Fact]
    public void ServiceAreaResolver_PrefersTheMunicipality_ThenFallsBackToTheDistrict()
    {
        var district = ServiceArea.Create("P3-KTM", "Kathmandu", Province.Bagmati, "Kathmandu", 1);
        var municipality = ServiceArea.Create("P3-KTM-KMC", "Kathmandu city", Province.Bagmati, "Kathmandu", 1, "Kathmandu Metropolitan City");
        var address = TestData.KathmanduAddress();

        Assert.Same(municipality, ServiceAreaResolver.Resolve([district, municipality], address));
        Assert.Same(district, ServiceAreaResolver.Resolve([district], address));
        Assert.Same(district, ServiceAreaResolver.Resolve([municipality, district], NepalAddress.Create(Province.Bagmati, "kathmandu", "Budhanilkantha", 3)));
    }

    [Fact]
    public void ServiceAreaResolver_ReturnsNull_ForADistrictWeDoNotCover()
    {
        var area = ServiceArea.Create("P3-KTM", "Kathmandu", Province.Bagmati, "Kathmandu", 1);

        Assert.Null(ServiceAreaResolver.Resolve([area], TestData.PokharaAddress()));
        Assert.Null(ServiceAreaResolver.Resolve([], TestData.PokharaAddress()));
    }

    [Fact]
    public void NepalDistricts_ListsAll77Districts_InTheRightProvinces()
    {
        Assert.Equal(77, NepalDistricts.All.Count());
        Assert.Equal(77, NepalDistricts.All.Select(d => d.District).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            [14, 8, 13, 11, 12, 10, 9],
            Enum.GetValues<Province>().OrderBy(p => (int)p).Select(p => NepalDistricts.For(p).Count).ToArray());
    }

    [Theory]
    [InlineData(Province.Bagmati, "Kathmandu", true)]
    [InlineData(Province.Bagmati, " kathmandu ", true)]
    [InlineData(Province.Gandaki, "Kaski", true)]
    [InlineData(Province.Gandaki, "Kathmandu", false)]
    [InlineData(Province.Bagmati, "Atlantis", false)]
    [InlineData(Province.Bagmati, "", false)]
    [InlineData(Province.Bagmati, null, false)]
    public void NepalDistricts_Contains_ChecksTheDistrictBelongsToTheProvince(Province province, string? district, bool expected) =>
        Assert.Equal(expected, NepalDistricts.Contains(province, district));

    // ---- pricing rule ------------------------------------------------------------------------------------------

    [Fact]
    public void PricingRule_Specificity_CountsTheAreasItNames()
    {
        Assert.Equal(0, TestData.Rule().Specificity);
        Assert.Equal(1, TestData.Rule(fromArea: Guid.NewGuid()).Specificity);
        Assert.Equal(2, TestData.Rule(fromArea: Guid.NewGuid(), toArea: Guid.NewGuid()).Specificity);
    }

    [Fact]
    public void PricingRule_IsEffectiveAt_RespectsTheValidityWindow()
    {
        var rule = TestData.Rule(from: Now, to: Now.AddDays(10));

        Assert.False(rule.IsEffectiveAt(Now.AddSeconds(-1)));
        Assert.True(rule.IsEffectiveAt(Now));
        Assert.True(rule.IsEffectiveAt(Now.AddDays(10).AddSeconds(-1)));
        Assert.False(rule.IsEffectiveAt(Now.AddDays(10)));
    }

    [Fact]
    public void PricingRule_Deactivate_StopsItFromApplying()
    {
        var rule = TestData.Rule();

        rule.Deactivate(Now);

        Assert.False(rule.IsEffectiveAt(Now));
        Assert.Equal(Now, rule.EffectiveToUtc);
    }
}
