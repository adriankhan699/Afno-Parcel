using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;
using NepalParcel.Domain.Pricing;
using NSubstitute;

namespace NepalParcel.UnitTests.Application;

public sealed class ParcelServiceTests
{
    private readonly IParcelRepository _parcels = Substitute.For<IParcelRepository>();
    private readonly IPaymentRepository _payments = Substitute.For<IPaymentRepository>();
    private readonly IBranchRepository _branches = Substitute.For<IBranchRepository>();
    private readonly IWarehouseRepository _warehouses = Substitute.For<IWarehouseRepository>();
    private readonly ICustomerRepository _customers = Substitute.For<ICustomerRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IPricingRepository _pricing = Substitute.For<IPricingRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IClientInfo _client = Substitute.For<IClientInfo>();
    private readonly FixedClock _clock = new(TestData.Now);

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Branch _origin = Branch.Create("KTM01", "Kathmandu Main", BranchType.Branch, TestData.KathmanduAddress(), "9841000000");
    private readonly ServiceArea _kathmanduArea = ServiceArea.Create("P3-KATHMANDU", "Kathmandu District", Province.Bagmati, "Kathmandu", 1);
    private readonly ServiceArea _pokharaArea = ServiceArea.Create("P4-KASKI", "Kaski District", Province.Gandaki, "Kaski", 3, servingBranchId: Guid.NewGuid());
    private readonly Customer _customer;
    private readonly ParcelService _sut;

    public ParcelServiceTests()
    {
        _currentUser.UserId.Returns(_userId);
        _currentUser.UserName.Returns("Test User");
        _customer = Customer.Create("Ram Sharma", "9841234567", userId: _userId);

        _branches.GetByIdAsync(_origin.Id, Arg.Any<CancellationToken>()).Returns(_origin);
        _branches.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Branch> { [_origin.Id] = _origin });

        _pricing.GetServiceAreasInDistrictAsync(Province.Bagmati, "Kathmandu", Arg.Any<CancellationToken>()).Returns([_kathmanduArea]);
        _pricing.GetServiceAreasInDistrictAsync(Province.Gandaki, "Kaski", Arg.Any<CancellationToken>()).Returns([_pokharaArea]);
        _pricing.GetEffectiveRulesAsync(ServiceType.Standard, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([TestData.Rule(fromArea: null, toArea: null)]);

        _customers.GetByUserIdAsync(_userId, Arg.Any<CancellationToken>()).Returns(_customer);
        _parcels.TrackingNumberExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var options = Options.Create(new BookingOptions { Receipt = { CompanyName = "Afno Parcel Test", CompanyPhone = "01-4000000" } });
        _sut = new ParcelService(
            _parcels, _payments, _branches, _warehouses, _customers, _audit,
            new PriceEngine(_pricing, options),
            _unitOfWork, _currentUser, _client, _clock, options,
            new BookParcelRequestValidator(options),
            new ParcelSearchQueryValidator(),
            new CancelParcelRequestValidator(),
            new ConfirmPaymentRequestValidator(),
            NullLogger<ParcelService>.Instance);
    }

    private static (IReadOnlyList<ParcelListItem> Items, int TotalCount) EmptyPage(int totalCount = 0) =>
        (Array.Empty<ParcelListItem>(), totalCount);

    private static (IReadOnlyList<ParcelListItem> Items, int TotalCount) PageOf(params ParcelListItem[] rows) =>
        (rows, rows.Length);

    private void SignInAs(string role)
    {
        _currentUser.IsInRole(Arg.Any<string>()).Returns(false);
        _currentUser.IsInRole(role).Returns(true);
    }

    private BookParcelRequest Request(string payment = "Cash", decimal? cod = null, string service = "Standard") =>
        new(
            new BookingPartyDto("Ram Sharma", "984-1234567", TestData.KathmanduAddressDto(), "Call first"),
            new BookingPartyDto("Sita Gurung", "9851234567", TestData.PokharaAddressDto(), "Blue gate"),
            _origin.Id,
            "Books",
            2,
            2.4m,
            service,
            payment,
            CodAmount: cod,
            PaymentReference: "REF-1");

    // ---- booking -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Book_AsCustomer_ChargesTheServerCalculatedPrice_AndLeavesPaymentPending()
    {
        SignInAs(Roles.Customer);
        Parcel? saved = null;
        Payment? payment = null;
        _parcels.When(p => p.Add(Arg.Any<Parcel>())).Do(c => saved = c.Arg<Parcel>());
        _payments.When(p => p.Add(Arg.Any<Payment>())).Do(c => payment = c.Arg<Payment>());

        var response = await _sut.BookAsync(Request());

        // 2.4 kg is priced as 2.5 kg: 150 + 45 × 1.5 = 217.50, plus 13% VAT (28.28) = 245.78.
        Assert.NotNull(saved);
        Assert.Equal(245.78m, saved.DeliveryCharge);
        Assert.Equal(245.78m, response.Price.TotalCharge);
        Assert.Equal(2.5m, response.Price.ChargeableWeightKg);
        Assert.Equal(BookingChannel.Online, saved.Channel);
        Assert.Equal(_customer.Id, saved.SenderCustomerId);
        Assert.Equal("BookingCreated", response.Status);
        Assert.Equal("Online", response.Channel);
        Assert.Equal(TestData.Now.AddDays(3), response.EstimatedDeliveryAtUtc);
        Assert.Equal(_pokharaArea.ServingBranchId, saved.DestinationBranchId);

        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(245.78m, payment.Amount);
        Assert.Equal("Pending", response.PaymentStatus);

        _customers.DidNotReceive().Add(Arg.Any<Customer>());
        _audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.ParcelBooked));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Book_TrackingNumber_IsGenerated_AndNormalisedPhonesAreStored()
    {
        SignInAs(Roles.Customer);

        var response = await _sut.BookAsync(Request());

        Assert.True(TrackingNumbers.IsValid(response.TrackingNumber));
        Assert.StartsWith("AFN", response.TrackingNumber, StringComparison.Ordinal);
        Assert.Equal("9841234567", response.Sender.PhoneNumber);
        Assert.Equal("Call first", response.Sender.AddressInstructions);
        Assert.Equal("Blue gate", response.Receiver.AddressInstructions);
    }

    [Fact]
    public async Task Book_TrackingNumberCollision_TriesAgain()
    {
        SignInAs(Roles.Customer);
        _parcels.TrackingNumberExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true, true, false);

        var response = await _sut.BookAsync(Request());

        Assert.True(TrackingNumbers.IsValid(response.TrackingNumber));
        await _parcels.Received(3).TrackingNumberExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Book_WhenEveryTrackingNumberIsTaken_ThrowsConflict()
    {
        SignInAs(Roles.Customer);
        _parcels.TrackingNumberExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.BookAsync(Request()));

        _parcels.DidNotReceive().Add(Arg.Any<Parcel>());
    }

    [Fact]
    public async Task Book_AsCounterStaff_CreatesAWalkInCustomer_AndMarksPrepaidPaymentPaid()
    {
        SignInAs(Roles.BookingStaff);
        Customer? walkIn = null;
        Payment? payment = null;
        _customers.When(c => c.Add(Arg.Any<Customer>())).Do(c => walkIn = c.Arg<Customer>());
        _payments.When(p => p.Add(Arg.Any<Payment>())).Do(c => payment = c.Arg<Payment>());

        var response = await _sut.BookAsync(Request());

        Assert.NotNull(walkIn);
        Assert.True(walkIn.IsWalkIn);
        Assert.Equal("9841234567", walkIn.PhoneNumber);
        Assert.Equal("Counter", response.Channel);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal("REF-1", payment.Reference);
        Assert.Equal("Paid", response.PaymentStatus);
        await _customers.DidNotReceive().GetByUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Book_AsCounterStaff_ReusesAnExistingWalkInCustomer()
    {
        SignInAs(Roles.BranchManager);
        var existing = Customer.Create("Ram Sharma", "9841234567");
        _customers.FindWalkInAsync("9841234567", "Ram Sharma", Arg.Any<CancellationToken>()).Returns(existing);
        Parcel? saved = null;
        _parcels.When(p => p.Add(Arg.Any<Parcel>())).Do(c => saved = c.Arg<Parcel>());

        await _sut.BookAsync(Request());

        Assert.Equal(existing.Id, saved!.SenderCustomerId);
        _customers.DidNotReceive().Add(Arg.Any<Customer>());
    }

    [Fact]
    public async Task Book_AtTheCounter_CashOnDelivery_StaysUnpaid_AndTheReceiverPaysChargePlusGoods()
    {
        SignInAs(Roles.BookingStaff);
        Parcel? saved = null;
        Payment? payment = null;
        _parcels.When(p => p.Add(Arg.Any<Parcel>())).Do(c => saved = c.Arg<Parcel>());
        _payments.When(p => p.Add(Arg.Any<Payment>())).Do(c => payment = c.Arg<Payment>());

        var response = await _sut.BookAsync(Request(payment: "CashOnDelivery", cod: 4000m));

        // 1.5% of 4000 = 60 COD fee; shipping 217.50; VAT 13% of 277.50 = 36.08; charge = 313.58; receiver pays 4000 + 313.58.
        Assert.Equal(313.58m, saved!.DeliveryCharge);
        Assert.Equal(60m, response.Price.CodFee);
        Assert.Equal(4313.58m, response.CodAmount);
        Assert.Equal(PaymentStatus.Pending, payment!.Status);
        Assert.Equal(PaymentMethod.CashOnDelivery, payment.Method);
    }

    [Fact]
    public async Task Book_PrepaidWithGoodsAmount_CollectsOnlyTheGoodsMoney()
    {
        SignInAs(Roles.Customer);

        var response = await _sut.BookAsync(Request(payment: "Esewa", cod: 4000m));

        Assert.Equal(4000m, response.CodAmount);
        Assert.Equal(313.58m, response.Price.TotalCharge);
    }

    [Fact]
    public async Task Book_PriceComesFromTheRules_NotFromTheRequest()
    {
        SignInAs(Roles.Customer);
        _pricing.GetEffectiveRulesAsync(ServiceType.Standard, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([TestData.Rule(basePrice: 1000m, pricePerKg: 0m)]);

        var response = await _sut.BookAsync(Request());

        Assert.Equal(1130m, response.Price.TotalCharge); // 1000 + 13% VAT
    }

    [Fact]
    public async Task Book_TheSameRequestPricedAtDifferentTimes_FollowsTheRuleThatIsInForce()
    {
        SignInAs(Roles.Customer);
        var before = await _sut.BookAsync(Request());

        _pricing.GetEffectiveRulesAsync(ServiceType.Standard, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([TestData.Rule(basePrice: 200m)]);
        var after = await _sut.BookAsync(Request());

        Assert.Equal(245.78m, before.Price.TotalCharge);
        Assert.Equal(302.28m, after.Price.TotalCharge); // 267.50 shipping + 34.78 VAT
    }

    [Fact]
    public async Task Book_UnknownOriginBranch_ThrowsNotFound()
    {
        SignInAs(Roles.Customer);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.BookAsync(Request() with { OriginBranchId = Guid.NewGuid() }));

        _parcels.DidNotReceive().Add(Arg.Any<Parcel>());
    }

    [Fact]
    public async Task Book_InactiveOriginBranch_ThrowsDomainException()
    {
        SignInAs(Roles.Customer);
        _origin.Deactivate();

        var ex = await Assert.ThrowsAsync<DomainException>(() => _sut.BookAsync(Request()));

        Assert.Contains("not accepting", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Book_DestinationWeDoNotServe_IsRefused_AndNothingIsSaved()
    {
        SignInAs(Roles.Customer);
        _pricing.GetServiceAreasInDistrictAsync(Province.Gandaki, "Kaski", Arg.Any<CancellationToken>()).Returns([]);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _sut.BookAsync(Request()));

        Assert.Contains("do not deliver to Kaski", ex.Message, StringComparison.Ordinal);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Book_ServiceAreaSwitchedOff_IsRefused()
    {
        SignInAs(Roles.Customer);
        _pokharaArea.SetServiceable(false);

        await Assert.ThrowsAsync<DomainException>(() => _sut.BookAsync(Request()));
    }

    [Fact]
    public async Task Book_NoPriceForTheServiceOnThisRoute_IsRefused()
    {
        SignInAs(Roles.Customer);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _sut.BookAsync(Request(service: "SameDay")));

        Assert.Contains("SameDay delivery is not available", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Book_InvalidRequest_ThrowsValidationException_BeforeAnythingIsLoaded()
    {
        SignInAs(Roles.Customer);

        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => _sut.BookAsync(Request() with { WeightKg = 0m, ServiceType = "Rocket" }));

        Assert.Contains("WeightKg", ex.Errors.Keys);
        Assert.Contains("ServiceType", ex.Errors.Keys);
        await _branches.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Book_Anonymous_ThrowsUnauthorized()
    {
        _currentUser.UserId.Returns((Guid?)null);

        await Assert.ThrowsAsync<UnauthorizedException>(() => _sut.BookAsync(Request()));
    }

    [Fact]
    public async Task Book_OnlineCustomerWithoutAProfile_GetsOneCreated()
    {
        SignInAs(Roles.Customer);
        _customers.GetByUserIdAsync(_userId, Arg.Any<CancellationToken>()).Returns((Customer?)null);
        Customer? created = null;
        _customers.When(c => c.Add(Arg.Any<Customer>())).Do(c => created = c.Arg<Customer>());

        await _sut.BookAsync(Request());

        Assert.NotNull(created);
        Assert.Equal(_userId, created.UserId);
    }

    // ---- reading -----------------------------------------------------------------------------------------------

    private Parcel StoredParcel(Guid? senderCustomerId = null, PaymentMethod method = PaymentMethod.Cash)
    {
        var details = TestData.ParcelDetails(method) with
        {
            SenderCustomerId = senderCustomerId ?? _customer.Id,
            OriginBranchId = _origin.Id,
            DestinationBranchId = null,
        };
        var parcel = Parcel.Book(details, TestData.Now, _userId, "Test User");
        _parcels.GetByIdAsync(parcel.Id, Arg.Any<CancellationToken>()).Returns(parcel);
        _parcels.GetByTrackingNumberAsync(parcel.TrackingNumber, Arg.Any<CancellationToken>()).Returns(parcel);
        return parcel;
    }

    [Fact]
    public async Task GetById_Owner_SeesTheParcel_WithItsHistory()
    {
        SignInAs(Roles.Customer);
        var parcel = StoredParcel();

        var response = await _sut.GetByIdAsync(parcel.Id);

        Assert.Equal(parcel.TrackingNumber, response.TrackingNumber);
        Assert.Equal("KTM01", response.OriginBranch.Code);
        Assert.Single(response.History);
        Assert.Equal("BookingCreated", response.History[0].Status);
        Assert.True(response.CanCancel);
    }

    [Fact]
    public async Task GetById_AnotherCustomersParcel_LooksLikeItDoesNotExist()
    {
        SignInAs(Roles.Customer);
        var parcel = StoredParcel(senderCustomerId: Guid.NewGuid());

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetByIdAsync(parcel.Id));
    }

    [Fact]
    public async Task GetById_CustomerWithoutAProfile_SeesNothing()
    {
        SignInAs(Roles.Customer);
        var parcel = StoredParcel();
        _customers.GetByUserIdAsync(_userId, Arg.Any<CancellationToken>()).Returns((Customer?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetByIdAsync(parcel.Id));
    }

    [Theory]
    [InlineData(Roles.SuperAdmin)]
    [InlineData(Roles.BranchManager)]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.Accountant)]
    public async Task GetById_StaffRoles_SeeAnyParcel(string role)
    {
        SignInAs(role);
        var parcel = StoredParcel(senderCustomerId: Guid.NewGuid());

        var response = await _sut.GetByIdAsync(parcel.Id);

        Assert.Equal(parcel.TrackingNumber, response.TrackingNumber);
    }

    [Fact]
    public async Task GetById_Unknown_ThrowsNotFound()
    {
        SignInAs(Roles.BookingStaff);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetByIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetByTrackingNumber_AcceptsLowerCaseAndDashes()
    {
        SignInAs(Roles.Customer);
        var parcel = StoredParcel();
        var typed = parcel.TrackingNumber.ToLowerInvariant().Insert(3, "-");

        var response = await _sut.GetByTrackingNumberAsync(typed);

        Assert.Equal(parcel.Id, response.Id);
    }

    [Fact]
    public async Task GetByTrackingNumber_Malformed_ThrowsNotFound_WithoutQueryingTheDatabase()
    {
        SignInAs(Roles.BookingStaff);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetByTrackingNumberAsync("NOT-A-TRACKING-NUMBER"));

        await _parcels.DidNotReceive().GetByTrackingNumberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- searching ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_AsCustomer_IsRestrictedToTheirOwnParcels()
    {
        SignInAs(Roles.Customer);
        ParcelFilter? filter = null;
        _parcels.SearchAsync(Arg.Do<ParcelFilter>(f => filter = f), 1, 20, Arg.Any<CancellationToken>())
            .Returns(EmptyPage());

        await _sut.SearchAsync(new ParcelSearchQuery { Search = "  AFN  " });

        Assert.Equal(_customer.Id, filter!.SenderCustomerId);
        Assert.Equal("AFN", filter.Search);
    }

    [Fact]
    public async Task Search_AsCustomerWithoutAProfile_CannotMatchAnything()
    {
        SignInAs(Roles.Customer);
        _customers.GetByUserIdAsync(_userId, Arg.Any<CancellationToken>()).Returns((Customer?)null);
        ParcelFilter? filter = null;
        _parcels.SearchAsync(Arg.Do<ParcelFilter>(f => filter = f), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(EmptyPage());

        await _sut.SearchAsync(new ParcelSearchQuery());

        Assert.Equal(Guid.Empty, filter!.SenderCustomerId);
    }

    [Fact]
    public async Task Search_AsStaff_SeesEveryCustomer_AndPassesTheFiltersThrough()
    {
        SignInAs(Roles.BookingStaff);
        ParcelFilter? filter = null;
        var branchId = Guid.NewGuid();
        _parcels.SearchAsync(Arg.Do<ParcelFilter>(f => filter = f), 2, 10, Arg.Any<CancellationToken>())
            .Returns(EmptyPage(25));

        var page = await _sut.SearchAsync(new ParcelSearchQuery
        {
            Page = 2,
            PageSize = 10,
            Status = "inTransit",
            ServiceType = "Express",
            PaymentMethod = "CashOnDelivery",
            Channel = "Counter",
            IsCod = true,
            OriginBranchId = branchId,
        });

        Assert.Null(filter!.SenderCustomerId);
        Assert.Equal(ParcelStatusCode.InTransit, filter.Status);
        Assert.Equal(ServiceType.Express, filter.ServiceType);
        Assert.Equal(PaymentMethod.CashOnDelivery, filter.PaymentMethod);
        Assert.Equal(BookingChannel.Counter, filter.Channel);
        Assert.True(filter.IsCod);
        Assert.Equal(branchId, filter.OriginBranchId);
        Assert.Equal(25, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
        Assert.True(page.HasNextPage);
    }

    [Fact]
    public async Task Search_Dates_AreNepalCalendarDays_ConvertedToUtc()
    {
        SignInAs(Roles.BookingStaff);
        ParcelFilter? filter = null;
        _parcels.SearchAsync(Arg.Do<ParcelFilter>(f => filter = f), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(EmptyPage());

        await _sut.SearchAsync(new ParcelSearchQuery { BookedFrom = new DateOnly(2026, 11, 18), BookedTo = new DateOnly(2026, 11, 18) });

        // Nepal is UTC+05:45, so 18 Nov starts at 17 Nov 18:15 UTC and the day ends (exclusive) at 18 Nov 18:15 UTC.
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 18, 15, 0, TimeSpan.Zero), filter!.BookedFromUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 18, 15, 0, TimeSpan.Zero), filter.BookedBeforeUtc);
    }

    [Fact]
    public async Task Search_MapsRowsToSummaries()
    {
        SignInAs(Roles.BookingStaff);
        var row = new ParcelListItem(
            Guid.NewGuid(), "AFNABCDEFGHJK2", (int)ParcelStatusCode.InTransit, ServiceType.Express, BookingChannel.Online,
            PaymentMethod.Esewa, PaymentStatus.Paid, "Ram", "Sita", "Kaski", "Pokhara", "Kathmandu Main", 2, 2.5m, 245.78m, 0m, TestData.Now);
        _parcels.SearchAsync(Arg.Any<ParcelFilter>(), 1, 20, Arg.Any<CancellationToken>()).Returns(PageOf(row));

        var page = await _sut.SearchAsync(new ParcelSearchQuery());

        var item = Assert.Single(page.Items);
        Assert.Equal("InTransit", item.Status);
        Assert.Equal("In transit", item.StatusName);
        Assert.Equal("Express", item.ServiceType);
        Assert.Equal("Paid", item.PaymentStatus);
        Assert.Equal(245.78m, item.DeliveryCharge);
    }

    [Fact]
    public async Task Search_InvalidQuery_ThrowsValidationException()
    {
        SignInAs(Roles.BookingStaff);

        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.SearchAsync(new ParcelSearchQuery { PageSize = 1000 }));
    }

    // ---- cancelling --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_ByTheCustomer_BeforePickup_CancelsTheBooking_AndVoidsAPendingPayment()
    {
        SignInAs(Roles.Customer);
        var parcel = StoredParcel();
        var payment = Payment.Create(parcel.Id, parcel.DeliveryCharge, PaymentMethod.Esewa, _customer.Id);
        _payments.GetLatestByParcelIdAsync(parcel.Id, Arg.Any<CancellationToken>()).Returns(payment);

        var response = await _sut.CancelAsync(parcel.Id, new CancelParcelRequest("Changed my mind"));

        Assert.Equal("Cancelled", response.Status);
        Assert.False(response.CanCancel);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Contains(response.History, h => h.Status == "Cancelled" && h.Remarks!.Contains("Changed my mind", StringComparison.Ordinal));
        _audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.ParcelCancelled));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_PaidBooking_FlagsThePaymentAsRefunded()
    {
        SignInAs(Roles.Customer);
        var parcel = StoredParcel();
        var payment = Payment.Create(parcel.Id, parcel.DeliveryCharge, PaymentMethod.Cash, _customer.Id);
        payment.MarkPaid(TestData.Now);
        _payments.GetLatestByParcelIdAsync(parcel.Id, Arg.Any<CancellationToken>()).Returns(payment);

        var response = await _sut.CancelAsync(parcel.Id, new CancelParcelRequest("Sent by mistake"));

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal("Refunded", response.PaymentStatus);
    }

    [Fact]
    public async Task Cancel_ByTheCustomer_AfterPickup_IsRefused_AndNothingIsSaved()
    {
        SignInAs(Roles.Customer);
        var parcel = StoredParcel();
        parcel.ChangeStatus(ParcelStatusCode.PickedUp, TestData.Now, null, null);

        await Assert.ThrowsAsync<DomainException>(() => _sut.CancelAsync(parcel.Id, new CancelParcelRequest("Changed my mind")));

        Assert.Equal(ParcelStatusCode.PickedUp, parcel.Status);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_ByCounterStaff_AfterPickup_IsAllowed()
    {
        SignInAs(Roles.BookingStaff);
        var parcel = StoredParcel(senderCustomerId: Guid.NewGuid());
        parcel.ChangeStatus(ParcelStatusCode.PickedUp, TestData.Now, null, null);

        var response = await _sut.CancelAsync(parcel.Id, new CancelParcelRequest("Customer asked at the counter"));

        Assert.Equal("Cancelled", response.Status);
    }

    [Fact]
    public async Task Cancel_AnotherCustomersParcel_LooksLikeItDoesNotExist()
    {
        SignInAs(Roles.Customer);
        var parcel = StoredParcel(senderCustomerId: Guid.NewGuid());

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.CancelAsync(parcel.Id, new CancelParcelRequest("Not mine to cancel")));

        Assert.Equal(ParcelStatusCode.BookingCreated, parcel.Status);
    }

    [Fact]
    public async Task Cancel_WithoutAReason_FailsValidation()
    {
        SignInAs(Roles.Customer);
        var parcel = StoredParcel();

        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.CancelAsync(parcel.Id, new CancelParcelRequest("no")));
    }

    [Fact]
    public async Task Cancel_Twice_IsRefused()
    {
        SignInAs(Roles.Customer);
        var parcel = StoredParcel();
        await _sut.CancelAsync(parcel.Id, new CancelParcelRequest("Changed my mind"));

        var ex = await Assert.ThrowsAsync<DomainException>(() => _sut.CancelAsync(parcel.Id, new CancelParcelRequest("Changed my mind")));

        Assert.Contains("already cancelled", ex.Message, StringComparison.Ordinal);
    }

    // ---- confirming payment ------------------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmPayment_MarksAPendingPrepaidPaymentPaid()
    {
        SignInAs(Roles.BookingStaff);
        var parcel = StoredParcel(method: PaymentMethod.Cash);
        var payment = Payment.Create(parcel.Id, parcel.DeliveryCharge, PaymentMethod.Cash, _customer.Id);
        _payments.GetLatestByParcelIdAsync(parcel.Id, Arg.Any<CancellationToken>()).Returns(payment);

        var response = await _sut.ConfirmPaymentAsync(parcel.Id, new ConfirmPaymentRequest("REC-77"));

        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal("REC-77", response.PaymentReference);
        _audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.PaymentConfirmed));
    }

    [Fact]
    public async Task ConfirmPayment_CashOnDelivery_IsRefused_BecauseTheRiderCollectsIt()
    {
        SignInAs(Roles.BookingStaff);
        var parcel = StoredParcel(method: PaymentMethod.CashOnDelivery);

        await Assert.ThrowsAsync<DomainException>(() => _sut.ConfirmPaymentAsync(parcel.Id, new ConfirmPaymentRequest()));
    }

    [Fact]
    public async Task ConfirmPayment_AlreadyPaid_IsAConflict()
    {
        SignInAs(Roles.BookingStaff);
        var parcel = StoredParcel(method: PaymentMethod.Cash);
        var payment = Payment.Create(parcel.Id, parcel.DeliveryCharge, PaymentMethod.Cash);
        payment.MarkPaid(TestData.Now);
        _payments.GetLatestByParcelIdAsync(parcel.Id, Arg.Any<CancellationToken>()).Returns(payment);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.ConfirmPaymentAsync(parcel.Id, new ConfirmPaymentRequest()));
    }

    [Fact]
    public async Task ConfirmPayment_CancelledBooking_IsRefused()
    {
        SignInAs(Roles.BookingStaff);
        var parcel = StoredParcel(method: PaymentMethod.Cash);
        parcel.Cancel("Customer changed mind", byStaff: true, TestData.Now, null, null);

        await Assert.ThrowsAsync<DomainException>(() => _sut.ConfirmPaymentAsync(parcel.Id, new ConfirmPaymentRequest()));
    }

    [Fact]
    public async Task ConfirmPayment_NoPaymentRecord_ThrowsNotFound()
    {
        SignInAs(Roles.BookingStaff);
        var parcel = StoredParcel(method: PaymentMethod.Cash);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ConfirmPaymentAsync(parcel.Id, new ConfirmPaymentRequest()));
    }

    // ---- receipt -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetReceipt_CarriesTheCompanyDetailsAndTheFullBooking()
    {
        SignInAs(Roles.Customer);
        var parcel = StoredParcel();

        var receipt = await _sut.GetReceiptAsync(parcel.Id);

        Assert.Equal($"R-{parcel.TrackingNumber}", receipt.ReceiptNumber);
        Assert.Equal("Afno Parcel Test", receipt.CompanyName);
        Assert.Equal("01-4000000", receipt.CompanyPhone);
        Assert.Equal(parcel.TrackingNumber, receipt.Parcel.TrackingNumber);
        Assert.Equal(parcel.DeliveryCharge, receipt.Parcel.Price.TotalCharge);
    }

    [Fact]
    public async Task GetReceipt_OfSomeoneElsesParcel_ThrowsNotFound()
    {
        SignInAs(Roles.Customer);
        var parcel = StoredParcel(senderCustomerId: Guid.NewGuid());

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetReceiptAsync(parcel.Id));
    }
}
