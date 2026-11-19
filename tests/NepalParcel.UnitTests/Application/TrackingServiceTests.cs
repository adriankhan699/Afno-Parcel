using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Notifications;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Tracking;
using NepalParcel.Domain.Warehouses;
using NepalParcel.UnitTests.Domain;
using NSubstitute;

namespace NepalParcel.UnitTests.Application;

/// <summary>A readable stand-in for the real keyed hashing and token signing.</summary>
internal sealed class FakeTrackingSecurity : ITrackingSecurity
{
    public string HashOtp(Guid parcelId, string code) => $"hash:{parcelId:N}:{code}";

    public bool VerifyOtp(Guid parcelId, string code, string expectedHash) => HashOtp(parcelId, code) == expectedHash;

    public string CreateAccessToken(string trackingNumber, DateTimeOffset expiresAtUtc) =>
        $"token|{trackingNumber}|{expiresAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";

    public bool ValidateAccessToken(string token, string trackingNumber, DateTimeOffset nowUtc)
    {
        var parts = token.Split('|');
        return parts.Length == 3 && parts[0] == "token" && parts[1] == trackingNumber && long.Parse(parts[2], CultureInfo.InvariantCulture) > nowUtc.ToUnixTimeSeconds();
    }
}

public sealed class TrackingServiceTests
{
    private readonly IParcelRepository _parcels = Substitute.For<IParcelRepository>();
    private readonly IBranchRepository _branches = Substitute.For<IBranchRepository>();
    private readonly IWarehouseRepository _warehouses = Substitute.For<IWarehouseRepository>();
    private readonly ICustomerRepository _customers = Substitute.For<ICustomerRepository>();
    private readonly ITrackingOtpRepository _otps = Substitute.For<ITrackingOtpRepository>();
    private readonly INotificationOutboxRepository _outbox = Substitute.For<INotificationOutboxRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IClientInfo _client = Substitute.For<IClientInfo>();
    private readonly FakeTrackingSecurity _security = new();
    private readonly FixedClock _clock = new(TestData.Now.AddDays(30));
    private readonly TrackingOptions _options = new();
    private readonly Branch _origin = Branch.Create("KTM01", "Kathmandu Main", BranchType.Branch, TestData.KathmanduAddress(), "9841000000");
    private readonly Warehouse _warehouse;
    private readonly Parcel _parcel;
    private readonly TrackingService _sut;

    public TrackingServiceTests()
    {
        _warehouse = Warehouse.Create(_origin.Id, "KTMW1", "Kathmandu Hub", TestData.KathmanduAddress(), "9841000001");

        // A parcel that has been picked up, shipped and has arrived at the hub, with staff remarks on it.
        var details = TestData.ParcelDetails() with { OriginBranchId = _origin.Id };
        _parcel = Parcel.Book(details, TestData.Now, Guid.NewGuid(), "Counter Staff");
        _parcel.ChangeStatus(ParcelStatusCode.PickedUp, TestData.Now.AddHours(1), Guid.NewGuid(), "Rider Ram", "Picked up from the shop, tel 9800000000");
        _parcel.ChangeStatus(ParcelStatusCode.Shipped, TestData.Now.AddHours(2), Guid.NewGuid(), "Clerk Sita", "Left on truck BA 1 KHA 2345", branchId: _origin.Id);
        _parcel.ChangeStatus(ParcelStatusCode.InTransit, TestData.Now.AddHours(3), Guid.NewGuid(), "Driver Hari");
        _parcel.ChangeStatus(ParcelStatusCode.ArrivedAtWarehouse, TestData.Now.AddHours(9), Guid.NewGuid(), "Hub Gita", "Damaged corner, repacked", warehouseId: _warehouse.Id);

        _parcels.GetByTrackingNumberAsync(_parcel.TrackingNumber, Arg.Any<CancellationToken>()).Returns(_parcel);
        _branches.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Branch> { [_origin.Id] = _origin });
        _warehouses.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Warehouse> { [_warehouse.Id] = _warehouse });
        _currentUser.IsAuthenticated.Returns(false);

        _sut = new TrackingService(
            _parcels, _branches, _warehouses, _customers, _otps, _outbox, _audit, _security, _unitOfWork, _currentUser, _client, _clock,
            Options.Create(_options),
            new VerifyTrackingOtpRequestValidator(),
            NullLogger<TrackingService>.Instance);
    }

    private static readonly string[] PrivateText =
    [
        "9851234567", "9841234567", "Sita", "Gurung", "Ram Sharma", "Lakeside", "Blue gate", "Everest", "New Baneshwor",
        "Counter Staff", "Rider Ram", "Clerk Sita", "Driver Hari", "Hub Gita", "tel 9800000000", "truck BA", "Damaged corner",
    ];

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    // ---- public tracking ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Public_ShowsStatusRouteEstimateAndTheCompleteTimeline()
    {
        var result = await _sut.GetPublicAsync(_parcel.TrackingNumber);

        Assert.Equal(_parcel.TrackingNumber, result.TrackingNumber);
        Assert.Equal("ArrivedAtWarehouse", result.Status);
        Assert.Equal("Arrived at warehouse", result.StatusName);
        Assert.Equal("The parcel has arrived at a warehouse.", result.StatusMessage);
        Assert.Equal("Kathmandu", result.OriginArea);
        Assert.Equal("Kaski", result.DestinationArea);
        Assert.Equal("Standard", result.ServiceType);
        Assert.False(result.IsFinal);
        Assert.Equal(
            ["BookingCreated", "PickedUp", "Shipped", "InTransit", "ArrivedAtWarehouse"],
            result.Events.Select(e => e.Status).ToArray());
        Assert.Equal(result.Events.OrderBy(e => e.OccurredAtUtc).Select(e => e.Status), result.Events.Select(e => e.Status));
        Assert.Equal("Kathmandu Main", result.Events[0].Location);
        Assert.Equal("Kathmandu Main", result.Events[2].Location);
        Assert.Null(result.Events[3].Location);
        Assert.Equal("Kathmandu Hub", result.Events[4].Location);
        Assert.Equal("The parcel has left the origin branch.", result.Events[2].Message);
    }

    [Fact]
    public async Task Public_NeverContainsPhoneNumbers_Names_Streets_Remarks_OrStaffNames()
    {
        var json = Json(await _sut.GetPublicAsync(_parcel.TrackingNumber));

        foreach (var secret in PrivateText)
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }

        // Nor amounts, ward numbers, municipality or postal codes.
        Assert.DoesNotContain("Pokhara", json, StringComparison.Ordinal);
        Assert.DoesNotContain("codAmount", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remarks", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("44600", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_AFinishedParcel_IsMarkedFinal()
    {
        var delivered = ParcelTests.ParcelAt(ParcelStatusCode.Delivered);
        _parcels.GetByTrackingNumberAsync(delivered.TrackingNumber, Arg.Any<CancellationToken>()).Returns(delivered);

        var result = await _sut.GetPublicAsync(delivered.TrackingNumber);

        Assert.True(result.IsFinal);
        Assert.Equal("Delivered", result.Status);
        Assert.NotNull(result.DeliveredAtUtc);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("AFNAAAAAAAAAAA")]
    [InlineData("")]
    public async Task Public_UnknownOrMalformedNumbers_AreNotFound(string input) =>
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetPublicAsync(input));

    [Fact]
    public async Task Public_AcceptsLowerCaseAndDashes()
    {
        var typed = _parcel.TrackingNumber.ToLowerInvariant().Insert(3, "-").Insert(8, " ");

        var result = await _sut.GetPublicAsync(typed);

        Assert.Equal(_parcel.TrackingNumber, result.TrackingNumber);
    }

    // ---- requesting a code -------------------------------------------------------------------------------------

    [Fact]
    public async Task RequestOtp_StoresOnlyAHash_AndQueuesAnSmsToTheReceiver()
    {
        TrackingOtp? stored = null;
        NotificationOutboxMessage? queued = null;
        _otps.When(o => o.Add(Arg.Any<TrackingOtp>())).Do(c => stored = c.Arg<TrackingOtp>());
        _outbox.When(o => o.Add(Arg.Any<NotificationOutboxMessage>())).Do(c => queued = c.Arg<NotificationOutboxMessage>());

        var response = await _sut.RequestOtpAsync(_parcel.TrackingNumber);

        Assert.NotNull(stored);
        Assert.NotNull(queued);
        Assert.Equal(_parcel.Id, stored.ParcelId);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(10), stored.ExpiresAtUtc);
        Assert.Equal(NotificationChannel.Sms, queued.Channel);
        Assert.Equal("9851234567", queued.Recipient);
        Assert.Equal("tracking-otp", queued.TemplateKey);
        Assert.Equal(_parcel.Id, queued.ParcelId);

        // The SMS carries a 6-digit code; the database row carries only a hash of that same code.
        using var payload = JsonDocument.Parse(queued.PayloadJson);
        var code = payload.RootElement.GetProperty("code").GetString()!;
        Assert.Matches("^[0-9]{6}$", code);
        Assert.Equal(_security.HashOtp(_parcel.Id, code), stored.CodeHash);

        Assert.Equal(10, response.ValidForMinutes);
        _audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.TrackingOtpRequested));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestOtp_GivesTheSameAnswer_ForUnknownNumbers_AndSendsNothing()
    {
        var known = await _sut.RequestOtpAsync(_parcel.TrackingNumber);
        _otps.ClearReceivedCalls();
        _outbox.ClearReceivedCalls();
        _unitOfWork.ClearReceivedCalls();

        var unknown = await _sut.RequestOtpAsync(TrackingNumbers.Generate());
        var malformed = await _sut.RequestOtpAsync("nonsense");

        Assert.Equal(known, unknown);
        Assert.Equal(known, malformed);
        _otps.DidNotReceive().Add(Arg.Any<TrackingOtp>());
        _outbox.DidNotReceive().Add(Arg.Any<NotificationOutboxMessage>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestOtp_TheResponseNeverMentionsThePhoneNumber()
    {
        var json = Json(await _sut.RequestOtpAsync(_parcel.TrackingNumber));

        Assert.DoesNotContain("9851234567", json, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"9[678]\d{8}", json);
        Assert.DoesNotContain("Sita", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestOtp_AfterTheHourlyLimit_IsSilentlyIgnored_WithTheSameAnswer()
    {
        _otps.CountSinceAsync(_parcel.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(3);

        var response = await _sut.RequestOtpAsync(_parcel.TrackingNumber);

        Assert.Equal(10, response.ValidForMinutes);
        _otps.DidNotReceive().Add(Arg.Any<TrackingOtp>());
        _outbox.DidNotReceive().Add(Arg.Any<NotificationOutboxMessage>());
        await _otps.Received(1).CountSinceAsync(_parcel.Id, _clock.GetUtcNow().AddHours(-1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestOtp_EveryCodeIsDifferent()
    {
        var codes = new List<string>();
        _outbox.When(o => o.Add(Arg.Any<NotificationOutboxMessage>())).Do(c =>
        {
            using var doc = JsonDocument.Parse(c.Arg<NotificationOutboxMessage>().PayloadJson);
            codes.Add(doc.RootElement.GetProperty("code").GetString()!);
        });

        for (var i = 0; i < 30; i++)
        {
            await _sut.RequestOtpAsync(_parcel.TrackingNumber);
        }

        Assert.True(codes.Distinct().Count() > 20, "codes should be random");
    }

    // ---- verifying a code --------------------------------------------------------------------------------------

    private TrackingOtp IssueCode(string code = "123456", TimeSpan? lifetime = null, DateTimeOffset? at = null)
    {
        var otp = TrackingOtp.Create(_parcel.Id, _security.HashOtp(_parcel.Id, code), at ?? _clock.GetUtcNow(), lifetime ?? TimeSpan.FromMinutes(10));
        _otps.GetLatestAsync(_parcel.Id, Arg.Any<CancellationToken>()).Returns(otp);
        return otp;
    }

    [Fact]
    public async Task Verify_ACorrectCode_GivesATokenForThisParcelOnly_AndUsesUpTheCode()
    {
        var otp = IssueCode();

        var access = await _sut.VerifyOtpAsync(_parcel.TrackingNumber, new VerifyTrackingOtpRequest("123456"));

        Assert.Equal(_clock.GetUtcNow().AddMinutes(15), access.ExpiresAtUtc);
        Assert.True(_security.ValidateAccessToken(access.AccessToken, _parcel.TrackingNumber, _clock.GetUtcNow()));
        Assert.False(_security.ValidateAccessToken(access.AccessToken, TrackingNumbers.Generate(), _clock.GetUtcNow()));
        Assert.NotNull(otp.ConsumedAtUtc);
        _audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.TrackingOtpVerified));
    }

    [Fact]
    public async Task Verify_ACodeWorksOnlyOnce()
    {
        IssueCode();
        await _sut.VerifyOtpAsync(_parcel.TrackingNumber, new VerifyTrackingOtpRequest("123456"));

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            _sut.VerifyOtpAsync(_parcel.TrackingNumber, new VerifyTrackingOtpRequest("123456")));
    }

    [Fact]
    public async Task Verify_AWrongCode_IsCounted_AndAnswersLikeEveryOtherFailure()
    {
        var otp = IssueCode();

        var ex = await Assert.ThrowsAsync<RequestValidationException>(() =>
            _sut.VerifyOtpAsync(_parcel.TrackingNumber, new VerifyTrackingOtpRequest("000000")));

        Assert.Equal(["Code"], ex.Errors.Keys.ToArray());
        Assert.Equal(1, otp.FailedAttempts);
        Assert.Null(otp.ConsumedAtUtc);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Verify_AfterFiveWrongGuesses_EvenTheRightCodeIsRefused()
    {
        IssueCode();
        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<RequestValidationException>(() =>
                _sut.VerifyOtpAsync(_parcel.TrackingNumber, new VerifyTrackingOtpRequest("999999")));
        }

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            _sut.VerifyOtpAsync(_parcel.TrackingNumber, new VerifyTrackingOtpRequest("123456")));
    }

    [Fact]
    public async Task Verify_AnExpiredCode_IsRefused()
    {
        IssueCode(at: _clock.GetUtcNow().AddMinutes(-11));

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            _sut.VerifyOtpAsync(_parcel.TrackingNumber, new VerifyTrackingOtpRequest("123456")));
    }

    [Fact]
    public async Task Verify_UnknownNumber_NoCodeRequested_And_MalformedNumber_AllFailTheSameWay()
    {
        var failures = new List<RequestValidationException>
        {
            await Assert.ThrowsAsync<RequestValidationException>(() => _sut.VerifyOtpAsync(TrackingNumbers.Generate(), new VerifyTrackingOtpRequest("123456"))),
            await Assert.ThrowsAsync<RequestValidationException>(() => _sut.VerifyOtpAsync("nonsense", new VerifyTrackingOtpRequest("123456"))),
            await Assert.ThrowsAsync<RequestValidationException>(() => _sut.VerifyOtpAsync(_parcel.TrackingNumber, new VerifyTrackingOtpRequest("123456"))),
        };

        Assert.All(failures, f => Assert.Equal(failures[0].Errors["Code"], f.Errors["Code"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12 456")]
    [InlineData("abcdef")]
    public async Task Verify_AMalformedCode_FailsValidation(string code) =>
        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.VerifyOtpAsync(_parcel.TrackingNumber, new VerifyTrackingOtpRequest(code)));

    [Fact]
    public async Task Verify_OnlyTheNewestCodeCounts()
    {
        var older = TrackingOtp.Create(_parcel.Id, _security.HashOtp(_parcel.Id, "111111"), _clock.GetUtcNow().AddMinutes(-2), TimeSpan.FromMinutes(10));
        IssueCode("222222"); // the repository returns the newest one only
        Assert.NotNull(older);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            _sut.VerifyOtpAsync(_parcel.TrackingNumber, new VerifyTrackingOtpRequest("111111")));
        var ok = await _sut.VerifyOtpAsync(_parcel.TrackingNumber, new VerifyTrackingOtpRequest("222222"));

        Assert.False(string.IsNullOrEmpty(ok.AccessToken));
    }

    // ---- detailed tracking -------------------------------------------------------------------------------------

    private string TokenFor(string trackingNumber, int minutes = 15) =>
        _security.CreateAccessToken(trackingNumber, _clock.GetUtcNow().AddMinutes(minutes));

    [Fact]
    public async Task Details_ForAVerifiedReceiver_ShowTheReceiversOwnInformation_ButNoRemarks()
    {
        var details = await _sut.GetDetailsAsync(_parcel.TrackingNumber, TokenFor(_parcel.TrackingNumber));

        Assert.Equal("Sita Gurung", details.ReceiverName);
        Assert.Equal("Pokhara Metropolitan City-8, Kaski", details.ReceiverArea);
        Assert.Equal(_parcel.CodAmount, details.CodAmount);
        Assert.Equal("Cash", details.PaymentMethod);
        Assert.Equal(5, details.Events.Count);
        Assert.All(details.Events, e => Assert.Null(e.Remarks));

        var json = Json(details);
        foreach (var secret in new[] { "9851234567", "9841234567", "Ram Sharma", "Lakeside", "Blue gate", "Everest", "Rider Ram", "Hub Gita", "Damaged corner", "truck BA", "tel 9800000000" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Details_WithoutATokenOrASignIn_AreRefused()
    {
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => _sut.GetDetailsAsync(_parcel.TrackingNumber, null));

        Assert.Contains("one-time code", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("token|AFNAAAAAAAAAAA|9999999999")]
    [InlineData("")]
    public async Task Details_WithABadToken_AreRefused(string token) =>
        await Assert.ThrowsAsync<UnauthorizedException>(() => _sut.GetDetailsAsync(_parcel.TrackingNumber, token));

    [Fact]
    public async Task Details_ATokenForAnotherParcel_IsRefused()
    {
        var other = ParcelTests.ParcelAt(ParcelStatusCode.PickedUp);
        _parcels.GetByTrackingNumberAsync(other.TrackingNumber, Arg.Any<CancellationToken>()).Returns(other);

        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.GetDetailsAsync(other.TrackingNumber, TokenFor(_parcel.TrackingNumber)));
    }

    [Fact]
    public async Task Details_AnExpiredToken_IsRefused() =>
        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.GetDetailsAsync(_parcel.TrackingNumber, TokenFor(_parcel.TrackingNumber, minutes: -1)));

    [Fact]
    public async Task Details_AValidTokenForAnUnknownParcel_IsNotFound()
    {
        var unknown = TrackingNumbers.Generate();

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetDetailsAsync(unknown, TokenFor(unknown)));
    }

    [Fact]
    public async Task Details_ForTheSignedInSender_IncludeRemarks()
    {
        var userId = Guid.NewGuid();
        var customer = Customer.Create("Ram Sharma", "9841234567", userId: userId);
        var owned = Parcel.Book(TestData.ParcelDetails() with { SenderCustomerId = customer.Id, OriginBranchId = _origin.Id }, TestData.Now);
        owned.ChangeStatus(ParcelStatusCode.PickedUp, TestData.Now.AddHours(1), null, null, "Collected from the shop");
        _parcels.GetByTrackingNumberAsync(owned.TrackingNumber, Arg.Any<CancellationToken>()).Returns(owned);
        _currentUser.IsAuthenticated.Returns(true);
        _currentUser.UserId.Returns(userId);
        _customers.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns(customer);

        var details = await _sut.GetDetailsAsync(owned.TrackingNumber, null);

        Assert.Contains(details.Events, e => e.Remarks == "Collected from the shop");
    }

    [Fact]
    public async Task Details_ForAnotherCustomer_LookLikeTheParcelDoesNotExist()
    {
        var stranger = Guid.NewGuid();
        _currentUser.IsAuthenticated.Returns(true);
        _currentUser.UserId.Returns(stranger);
        _customers.GetByUserIdAsync(stranger, Arg.Any<CancellationToken>()).Returns(Customer.Create("Someone Else", "9801234567", userId: stranger));

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetDetailsAsync(_parcel.TrackingNumber, null));
    }

    [Theory]
    [InlineData(Roles.SuperAdmin)]
    [InlineData(Roles.BranchManager)]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.Accountant)]
    public async Task Details_ForStaff_IncludeRemarksOfAnyParcel(string role)
    {
        _currentUser.IsAuthenticated.Returns(true);
        _currentUser.UserId.Returns(Guid.NewGuid());
        _currentUser.IsInRole(Arg.Any<string>()).Returns(false);
        _currentUser.IsInRole(role).Returns(true);

        var details = await _sut.GetDetailsAsync(_parcel.TrackingNumber, null);

        Assert.Contains(details.Events, e => e.Remarks == "Damaged corner, repacked");
    }

    [Fact]
    public async Task Details_ADeliveryAgent_HasNoGeneralAccess()
    {
        _currentUser.IsAuthenticated.Returns(true);
        _currentUser.UserId.Returns(Guid.NewGuid());
        _currentUser.IsInRole(Arg.Any<string>()).Returns(false);
        _currentUser.IsInRole(Roles.DeliveryAgent).Returns(true);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetDetailsAsync(_parcel.TrackingNumber, null));
    }
}
