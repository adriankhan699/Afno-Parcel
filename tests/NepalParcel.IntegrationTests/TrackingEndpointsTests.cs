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

/// <summary>Public tracking, the privacy limits on it, and detailed tracking through a one-time code or a sign-in.</summary>
public sealed class TrackingEndpointsTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private const string AccessHeader = "X-Tracking-Access";
    private readonly BookingTestHelpers _helpers = new(factory);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>())!;

    private static Uri TrackUri(string trackingNumber, string suffix = "") => new($"/api/v1/tracking/{trackingNumber}{suffix}", UriKind.Relative);

    private sealed record Booked(ParcelResponse Parcel, HttpClient Customer, string ReceiverName, string ReceiverPhone, string SenderName, Guid Branch);

    /// <summary>A parcel with recognisable private details, so tests can prove none of them leak.</summary>
    private async Task<Booked> BookWithPrivateDetailsAsync(decimal? cod = null)
    {
        var customer = await _helpers.CustomerAsync();
        var branch = await _helpers.CreateBranchAsync();
        var receiver = NewName("Receiver");
        var phone = NewPhone();
        var sender = NewName("Sender");
        var parcel = await BookAsync(customer, NewBooking(branch, receiverName: receiver, receiverPhone: phone, senderName: sender, cod: cod));
        return new Booked(parcel, customer, receiver, phone, sender, branch);
    }

    private async Task<string> VerifiedTokenAsync(Booked booked)
    {
        var client = factory.CreateClient();
        (await client.PostAsync(TrackUri(booked.Parcel.TrackingNumber, "/otp"), null)).EnsureSuccessStatusCode();
        var code = await _helpers.LatestOtpCodeAsync(booked.Parcel.Id);
        var response = await client.PostAsJsonAsync(TrackUri(booked.Parcel.TrackingNumber, "/verify"), new VerifyTrackingOtpRequest(code));
        response.EnsureSuccessStatusCode();
        return (await ReadAsync<TrackingAccessResponse>(response)).AccessToken;
    }

    private static HttpRequestMessage DetailsRequest(string trackingNumber, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, TrackUri(trackingNumber, "/details"));
        if (token is not null)
        {
            request.Headers.Add(AccessHeader, token);
        }

        return request;
    }

    // ---- public tracking ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Anyone_CanTrackWithJustTheTrackingNumber_AndSeeTheStatusAndTimeline()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync(TrackUri(booked.Parcel.TrackingNumber));
        var tracking = await ReadAsync<PublicTrackingResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(booked.Parcel.TrackingNumber, tracking.TrackingNumber);
        Assert.Equal("BookingCreated", tracking.Status);
        Assert.Equal("Booking created", tracking.StatusName);
        Assert.Equal("Your booking has been created.", tracking.StatusMessage);
        Assert.Equal("Kathmandu", tracking.OriginArea);
        Assert.Equal("Kaski", tracking.DestinationArea);
        Assert.Equal("Standard", tracking.ServiceType);
        Assert.False(tracking.IsFinal);
        Assert.NotNull(tracking.EstimatedDeliveryAtUtc);
        var first = Assert.Single(tracking.Events);
        Assert.Equal("Test Branch", first.Location);
    }

    [Fact]
    public async Task PublicTracking_NeverRevealsPhoneNumbers_Names_Addresses_Amounts_Remarks_OrStaff()
    {
        var booked = await BookWithPrivateDetailsAsync(cod: 4321m);
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);
        (await counter.PostAsJsonAsync(StatusUri(booked.Parcel.Id), To("PickedUp", "Spoke to Hari on 9807654321 about the gate code 4455"))).EnsureSuccessStatusCode();

        var json = await factory.CreateClient().GetStringAsync(TrackUri(booked.Parcel.TrackingNumber));

        foreach (var secret in new[]
                 {
                     booked.ReceiverPhone, "9841234567", booked.ReceiverName, booked.SenderName, "Sita", "Gurung", "Lakeside", "Blue gate",
                     "Call before arriving", "New Baneshwor", "Pokhara", "Spoke to Hari", "9807654321", "gate code", "Pulchowk",
                 })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }

        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("phone", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("receiver", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("sender", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("cod", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(doc.RootElement.GetProperty("events").EnumerateArray().SelectMany(e => e.EnumerateObject()).Select(p => p.Name), n => n is "remarks" or "changedBy");
    }

    [Fact]
    public async Task ThePublicTimeline_GrowsWithEveryStatusChange_AndKeepsEveryStep()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var warehouse = await _helpers.CreateWarehouseAsync(booked.Branch, "Kathmandu Hub");
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);
        var hub = await _helpers.StaffAsync(Roles.WarehouseStaff);
        var anonymous = factory.CreateClient();

        (await counter.PostAsJsonAsync(StatusUri(booked.Parcel.Id), To("PickedUp"))).EnsureSuccessStatusCode();
        (await counter.PostAsJsonAsync(StatusUri(booked.Parcel.Id), To("Shipped", branchId: booked.Branch))).EnsureSuccessStatusCode();
        var midway = await ReadAsync<PublicTrackingResponse>(await anonymous.GetAsync(TrackUri(booked.Parcel.TrackingNumber)));
        (await hub.PostAsJsonAsync(StatusUri(booked.Parcel.Id), To("InTransit"))).EnsureSuccessStatusCode();
        (await hub.PostAsJsonAsync(StatusUri(booked.Parcel.Id), To("ArrivedAtWarehouse", warehouseId: warehouse))).EnsureSuccessStatusCode();
        var later = await ReadAsync<PublicTrackingResponse>(await anonymous.GetAsync(TrackUri(booked.Parcel.TrackingNumber.ToLowerInvariant())));

        Assert.Equal("Shipped", midway.Status);
        Assert.Equal(3, midway.Events.Count);
        Assert.Equal("ArrivedAtWarehouse", later.Status);
        Assert.Equal("The parcel has arrived at a warehouse.", later.StatusMessage);
        Assert.Equal(["BookingCreated", "PickedUp", "Shipped", "InTransit", "ArrivedAtWarehouse"], later.Events.Select(e => e.Status).ToArray());
        Assert.Equal("Kathmandu Hub", later.Events[4].Location);
        Assert.Null(later.Events[3].Location);
        Assert.Equal(later.Events.OrderBy(e => e.OccurredAtUtc).Select(e => e.Status), later.Events.Select(e => e.Status));
    }

    [Fact]
    public async Task AFinishedParcel_IsMarkedFinal_WithItsDeliveryTime()
    {
        var booked = await BookWithPrivateDetailsAsync();
        await _helpers.AdvanceAsync(booked.Parcel.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.OutForDelivery, ParcelStatusCode.Delivered);

        var tracking = await ReadAsync<PublicTrackingResponse>(await factory.CreateClient().GetAsync(TrackUri(booked.Parcel.TrackingNumber)));

        Assert.True(tracking.IsFinal);
        Assert.Equal("Delivered", tracking.Status);
        Assert.NotNull(tracking.DeliveredAtUtc);
    }

    [Theory]
    [InlineData("AFNAAAAAAAAAAA")]
    [InlineData("nonsense")]
    [InlineData("NP12345678")]
    public async Task UnknownOrMalformedNumbers_Return404_WithTheSameAnswer(string number)
    {
        var response = await factory.CreateClient().GetAsync(TrackUri(number));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", ProblemMediaType(response));
    }

    // ---- detailed tracking needs proof -------------------------------------------------------------------------

    [Fact]
    public async Task DetailedTracking_WithoutACodeOrASignIn_Returns401()
    {
        var booked = await BookWithPrivateDetailsAsync();

        var response = await factory.CreateClient().SendAsync(DetailsRequest(booked.Parcel.TrackingNumber, null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", ProblemMediaType(response));
    }

    [Fact]
    public async Task ASenderSignedIn_SeesDetailedTracking_IncludingRemarks()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);
        (await counter.PostAsJsonAsync(StatusUri(booked.Parcel.Id), To("PickedUp", "Collected from the shop"))).EnsureSuccessStatusCode();

        var response = await booked.Customer.GetAsync(TrackUri(booked.Parcel.TrackingNumber, "/details"));
        var details = await ReadAsync<DetailedTrackingResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(booked.ReceiverName, details.ReceiverName);
        Assert.Equal("Pokhara Metropolitan City-8, Kaski", details.ReceiverArea);
        Assert.Contains(details.Events, e => e.Remarks == "Collected from the shop");
    }

    [Fact]
    public async Task AnotherSignedInCustomer_CannotSeeSomeoneElsesDetails()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var stranger = await _helpers.CustomerAsync();

        var response = await stranger.GetAsync(TrackUri(booked.Parcel.TrackingNumber, "/details"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(Roles.SuperAdmin)]
    [InlineData(Roles.BranchManager)]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.Accountant)]
    public async Task Staff_SeeDetailedTrackingOfAnyParcel(string role)
    {
        var booked = await BookWithPrivateDetailsAsync();
        var staff = await _helpers.StaffAsync(role);

        var response = await staff.GetAsync(TrackUri(booked.Parcel.TrackingNumber, "/details"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ADeliveryAgent_CannotBrowseDetailedTracking()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var rider = await _helpers.StaffAsync(Roles.DeliveryAgent);

        var response = await rider.GetAsync(TrackUri(booked.Parcel.TrackingNumber, "/details"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- receiver verification with a one-time code ------------------------------------------------------------

    [Fact]
    public async Task TheReceiver_CanVerifyWithTheCodeSentByText_AndSeeTheirOwnDetails_ButNoRemarks()
    {
        var booked = await BookWithPrivateDetailsAsync(cod: 2500m);
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);
        (await counter.PostAsJsonAsync(StatusUri(booked.Parcel.Id), To("PickedUp", "Internal note: owner is rude"))).EnsureSuccessStatusCode();
        var anonymous = factory.CreateClient();

        var requested = await anonymous.PostAsync(TrackUri(booked.Parcel.TrackingNumber, "/otp"), null);
        var code = await _helpers.LatestOtpCodeAsync(booked.Parcel.Id);
        var verified = await anonymous.PostAsJsonAsync(TrackUri(booked.Parcel.TrackingNumber, "/verify"), new VerifyTrackingOtpRequest(code));
        var access = await ReadAsync<TrackingAccessResponse>(verified);
        var detailsResponse = await anonymous.SendAsync(DetailsRequest(booked.Parcel.TrackingNumber, access.AccessToken));
        var details = await ReadAsync<DetailedTrackingResponse>(detailsResponse);

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.True(access.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.Equal(HttpStatusCode.OK, detailsResponse.StatusCode);
        Assert.Equal(booked.ReceiverName, details.ReceiverName);
        Assert.Equal("Pokhara Metropolitan City-8, Kaski", details.ReceiverArea);
        Assert.Equal(booked.Parcel.CodAmount, details.CodAmount);
        Assert.True(details.CodAmount > 0);
        Assert.Equal("Cash", details.PaymentMethod);
        Assert.Equal(2, details.Events.Count);
        Assert.All(details.Events, e => Assert.Null(e.Remarks));

        // Even a verified receiver does not get phone numbers, the sender, the street or staff identities.
        var raw = await detailsResponse.Content.ReadAsStringAsync();
        foreach (var secret in new[] { booked.ReceiverPhone, "9841234567", booked.SenderName, "Lakeside", "Blue gate", "Call before arriving", "New Baneshwor", "rude", "Internal note" })
        {
            Assert.DoesNotContain(secret, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TheCodeRequest_AnswersTheSameWay_ForRealUnknownAndMalformedNumbers_AndNeverShowsThePhone()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var anonymous = factory.CreateClient();

        var real = await anonymous.PostAsync(TrackUri(booked.Parcel.TrackingNumber, "/otp"), null);
        var unknown = await anonymous.PostAsync(TrackUri("AFNAAAAAAAAAAA", "/otp"), null);
        var malformed = await anonymous.PostAsync(TrackUri("nonsense", "/otp"), null);
        var realBody = await real.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Accepted, real.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, malformed.StatusCode);
        Assert.Equal(realBody, await unknown.Content.ReadAsStringAsync());
        Assert.Equal(realBody, await malformed.Content.ReadAsStringAsync());
        Assert.DoesNotContain(booked.ReceiverPhone, realBody, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"9[678]\d{8}", realBody);
    }

    [Fact]
    public async Task TheCode_IsSentToTheReceiversPhone_AndStoredOnlyAsAHash()
    {
        var booked = await BookWithPrivateDetailsAsync();
        (await factory.CreateClient().PostAsync(TrackUri(booked.Parcel.TrackingNumber, "/otp"), null)).EnsureSuccessStatusCode();
        var code = await _helpers.LatestOtpCodeAsync(booked.Parcel.Id);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var message = await db.NotificationOutbox.AsNoTracking().SingleAsync(m => m.ParcelId == booked.Parcel.Id);
        var otp = await db.TrackingOtps.AsNoTracking().SingleAsync(o => o.ParcelId == booked.Parcel.Id);
        var audit = await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == booked.Parcel.Id.ToString() && a.Action.StartsWith("TrackingOtp")).ToListAsync();

        Assert.Matches("^[0-9]{6}$", code);
        Assert.Equal(booked.ReceiverPhone, message.Recipient);
        Assert.Equal("Sms", message.Channel.ToString());
        Assert.Equal(64, otp.CodeHash.Length);
        Assert.Matches("^[0-9A-F]{64}$", otp.CodeHash);
        Assert.NotEqual(code, otp.CodeHash);
        Assert.True(otp.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.True(otp.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(11));
        Assert.Equal(0, otp.FailedAttempts);
        Assert.NotEmpty(audit);
        Assert.All(audit, a => Assert.DoesNotContain(code, a.NewValues ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWrongCode_Returns400_WithOneGenericMessage()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var anonymous = factory.CreateClient();
        (await anonymous.PostAsync(TrackUri(booked.Parcel.TrackingNumber, "/otp"), null)).EnsureSuccessStatusCode();
        var code = await _helpers.LatestOtpCodeAsync(booked.Parcel.Id);
        var wrong = code == "000000" ? "000001" : "000000";

        var response = await anonymous.PostAsJsonAsync(TrackUri(booked.Parcel.TrackingNumber, "/verify"), new VerifyTrackingOtpRequest(wrong));
        var errors = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("errors");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("incorrect or has expired", errors.GetProperty("Code")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryKindOfVerificationFailure_LooksIdentical()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var anonymous = factory.CreateClient();
        (await anonymous.PostAsync(TrackUri(booked.Parcel.TrackingNumber, "/otp"), null)).EnsureSuccessStatusCode();
        var code = await _helpers.LatestOtpCodeAsync(booked.Parcel.Id);
        var wrong = code == "000000" ? "000001" : "000000";

        async Task<string> Body(string number, string attempt) =>
            await (await anonymous.PostAsJsonAsync(TrackUri(number, "/verify"), new VerifyTrackingOtpRequest(attempt))).Content.ReadAsStringAsync();

        var wrongCode = await Body(booked.Parcel.TrackingNumber, wrong);
        var unknownNumber = await Body("AFNAAAAAAAAAAA", "123456");
        var noCodeRequested = await Body((await BookWithPrivateDetailsAsync()).Parcel.TrackingNumber, "123456");

        Assert.Equal(JsonDocument.Parse(wrongCode).RootElement.GetProperty("errors").ToString(), JsonDocument.Parse(unknownNumber).RootElement.GetProperty("errors").ToString());
        Assert.Equal(JsonDocument.Parse(wrongCode).RootElement.GetProperty("errors").ToString(), JsonDocument.Parse(noCodeRequested).RootElement.GetProperty("errors").ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public async Task AMalformedCode_Returns400(string code)
    {
        var booked = await BookWithPrivateDetailsAsync();

        var response = await factory.CreateClient().PostAsJsonAsync(TrackUri(booked.Parcel.TrackingNumber, "/verify"), new VerifyTrackingOtpRequest(code));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AfterFiveWrongGuesses_EvenTheCorrectCodeIsLocked_UntilANewCodeIsRequested()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var anonymous = factory.CreateClient();
        (await anonymous.PostAsync(TrackUri(booked.Parcel.TrackingNumber, "/otp"), null)).EnsureSuccessStatusCode();
        var code = await _helpers.LatestOtpCodeAsync(booked.Parcel.Id);
        var wrong = code == "000000" ? "000001" : "000000";

        for (var i = 0; i < 5; i++)
        {
            var miss = await anonymous.PostAsJsonAsync(TrackUri(booked.Parcel.TrackingNumber, "/verify"), new VerifyTrackingOtpRequest(wrong));
            Assert.Equal(HttpStatusCode.BadRequest, miss.StatusCode);
        }

        var locked = await anonymous.PostAsJsonAsync(TrackUri(booked.Parcel.TrackingNumber, "/verify"), new VerifyTrackingOtpRequest(code));
        Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);

        (await anonymous.PostAsync(TrackUri(booked.Parcel.TrackingNumber, "/otp"), null)).EnsureSuccessStatusCode();
        var fresh = await _helpers.LatestOtpCodeAsync(booked.Parcel.Id);
        var ok = await anonymous.PostAsJsonAsync(TrackUri(booked.Parcel.TrackingNumber, "/verify"), new VerifyTrackingOtpRequest(fresh));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task ACode_WorksOnlyOnce()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var anonymous = factory.CreateClient();
        (await anonymous.PostAsync(TrackUri(booked.Parcel.TrackingNumber, "/otp"), null)).EnsureSuccessStatusCode();
        var code = await _helpers.LatestOtpCodeAsync(booked.Parcel.Id);

        var first = await anonymous.PostAsJsonAsync(TrackUri(booked.Parcel.TrackingNumber, "/verify"), new VerifyTrackingOtpRequest(code));
        var second = await anonymous.PostAsJsonAsync(TrackUri(booked.Parcel.TrackingNumber, "/verify"), new VerifyTrackingOtpRequest(code));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task OnlyTheNewestCodeWorks()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var anonymous = factory.CreateClient();
        (await anonymous.PostAsync(TrackUri(booked.Parcel.TrackingNumber, "/otp"), null)).EnsureSuccessStatusCode();
        var older = await _helpers.LatestOtpCodeAsync(booked.Parcel.Id);
        await Task.Delay(20);
        (await anonymous.PostAsync(TrackUri(booked.Parcel.TrackingNumber, "/otp"), null)).EnsureSuccessStatusCode();
        var newer = await _helpers.LatestOtpCodeAsync(booked.Parcel.Id);

        if (older != newer)
        {
            var useOld = await anonymous.PostAsJsonAsync(TrackUri(booked.Parcel.TrackingNumber, "/verify"), new VerifyTrackingOtpRequest(older));
            Assert.Equal(HttpStatusCode.BadRequest, useOld.StatusCode);
        }

        var useNew = await anonymous.PostAsJsonAsync(TrackUri(booked.Parcel.TrackingNumber, "/verify"), new VerifyTrackingOtpRequest(newer));
        Assert.Equal(HttpStatusCode.OK, useNew.StatusCode);
    }

    [Fact]
    public async Task CodeRequests_AreLimitedPerParcelPerHour_WithoutTellingTheCaller()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var anonymous = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await anonymous.PostAsync(TrackUri(booked.Parcel.TrackingNumber, "/otp"), null)).StatusCode);
        }

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Accepted, s));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        Assert.Equal(3, await db.TrackingOtps.CountAsync(o => o.ParcelId == booked.Parcel.Id));
        Assert.Equal(3, await db.NotificationOutbox.CountAsync(m => m.ParcelId == booked.Parcel.Id));
    }

    // ---- access tokens -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ATokenWorksOnlyForTheParcelItWasIssuedFor()
    {
        var mine = await BookWithPrivateDetailsAsync();
        var other = await BookWithPrivateDetailsAsync();
        var token = await VerifiedTokenAsync(mine);
        var anonymous = factory.CreateClient();

        var ownResponse = await anonymous.SendAsync(DetailsRequest(mine.Parcel.TrackingNumber, token));
        var otherResponse = await anonymous.SendAsync(DetailsRequest(other.Parcel.TrackingNumber, token));

        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherResponse.StatusCode);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("a.b")]
    [InlineData("QUZOQUFBQUFBQUFBQUF8OTk5OTk5OTk5OQ.AAAA")]
    [InlineData("....")]
    public async Task ABadOrForgedToken_Returns401(string token)
    {
        var booked = await BookWithPrivateDetailsAsync();

        var response = await factory.CreateClient().SendAsync(DetailsRequest(booked.Parcel.TrackingNumber, token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ATamperedToken_Returns401()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var token = await VerifiedTokenAsync(booked);
        var parts = token.Split('.');
        var flipped = parts[1][0] == 'A' ? 'B' : 'A';
        var tampered = $"{parts[0]}.{flipped}{parts[1][1..]}";

        var response = await factory.CreateClient().SendAsync(DetailsRequest(booked.Parcel.TrackingNumber, tampered));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheTokenAndTheCode_AreNotInterchangeable()
    {
        var booked = await BookWithPrivateDetailsAsync();
        (await factory.CreateClient().PostAsync(TrackUri(booked.Parcel.TrackingNumber, "/otp"), null)).EnsureSuccessStatusCode();
        var code = await _helpers.LatestOtpCodeAsync(booked.Parcel.Id);

        var response = await factory.CreateClient().SendAsync(DetailsRequest(booked.Parcel.TrackingNumber, code));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AValidTokenForAnUnknownParcel_IsNotUsable()
    {
        var booked = await BookWithPrivateDetailsAsync();
        var token = await VerifiedTokenAsync(booked);

        var response = await factory.CreateClient().SendAsync(DetailsRequest("AFNAAAAAAAAAAA", token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
