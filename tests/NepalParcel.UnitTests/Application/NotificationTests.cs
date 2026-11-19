using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Notifications;
using NepalParcel.Application.Tracking;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Notifications;
using NepalParcel.Domain.Parcels;
using NSubstitute;

namespace NepalParcel.UnitTests.Application;

public sealed class NotificationRenderingTests
{
    [Fact]
    public void Parameters_AreListedInOrderOfFirstAppearance_EachOnce()
    {
        var names = TemplateRenderer.Parameters("Parcel {tracking}: {link} (again {tracking}) by {service}");

        Assert.Equal(["tracking", "link", "service"], names);
    }

    [Fact]
    public void Render_FillsPlaceholders_AndAMissingValueBecomesEmpty()
    {
        var text = TemplateRenderer.Render(
            "Parcel {tracking} {link}{eta}",
            new Dictionary<string, string> { ["tracking"] = "NP-1", ["link"] = "https://x.test/track/NP-1" });

        Assert.Equal("Parcel NP-1 https://x.test/track/NP-1", text);
    }

    [Fact]
    public void ParameterValues_FollowTheOrderTheTemplateUsesThem()
    {
        var values = TemplateRenderer.ParameterValues(
            "{link} then {tracking}",
            new Dictionary<string, string> { ["tracking"] = "NP-1", ["link"] = "L" });

        Assert.Equal(["L", "NP-1"], values);
    }

    [Fact]
    public void AnOldStylePayload_IsGivenTheNamesTemplatesUse()
    {
        var values = TemplateRenderer.ValuesFromPayload("{\"code\":\"123456\",\"trackingNumber\":\"NP-9\",\"validForMinutes\":10}");

        Assert.Equal("123456", values["code"]);
        Assert.Equal("NP-9", values["tracking"]);
        Assert.Equal("10", values["minutes"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void ABrokenPayload_GivesNoValuesInsteadOfAnError(string payload)
    {
        Assert.Empty(TemplateRenderer.ValuesFromPayload(payload));
    }

    [Fact]
    public void Validate_FindsUnknownPlaceholders_StrayBrackets_AndEmptyText()
    {
        Assert.Empty(TemplateRenderer.Validate(NotificationTemplateKeys.ParcelShipped, NotificationChannel.Sms, "Parcel {tracking} {link}"));
        Assert.Contains(
            TemplateRenderer.Validate(NotificationTemplateKeys.ParcelShipped, NotificationChannel.Sms, "Hello {receiverName}"),
            p => p.Contains("receiverName", StringComparison.Ordinal));
        Assert.NotEmpty(TemplateRenderer.Validate(NotificationTemplateKeys.ParcelShipped, NotificationChannel.Sms, "Parcel {tracking"));
        Assert.NotEmpty(TemplateRenderer.Validate(NotificationTemplateKeys.ParcelShipped, NotificationChannel.Sms, "Parcel tracking}"));
        Assert.NotEmpty(TemplateRenderer.Validate(NotificationTemplateKeys.ParcelShipped, NotificationChannel.Sms, "  "));
    }

    [Fact]
    public void NamesAndFailureReasons_CannotBePutInAMessage()
    {
        var allowed = TemplatePlaceholders.Allowed(NotificationTemplateKeys.ParcelDeliveryFailed);

        Assert.DoesNotContain("reason", allowed);
        Assert.DoesNotContain("receiver", allowed);
        Assert.DoesNotContain("sender", allowed);
        Assert.DoesNotContain("phone", allowed);
    }

    [Fact]
    public void AnSmsThatWouldBeSplitIntoManyParts_IsRefused()
    {
        var problems = TemplateRenderer.Validate(NotificationTemplateKeys.ParcelShipped, NotificationChannel.Sms, new string('a', TemplateRenderer.MaxSmsLength + 1));

        Assert.NotEmpty(problems);
    }

    [Fact]
    public void EveryDefaultTemplate_IsValid_AndEveryMessageHasAnSmsText()
    {
        foreach (var seed in DefaultTemplates.All)
        {
            Assert.Empty(TemplateRenderer.Validate(seed.Key, seed.Channel, seed.Body));
        }

        foreach (var key in NotificationTemplateKeys.All)
        {
            Assert.Contains(DefaultTemplates.All, s => s.Key == key && s.Channel == NotificationChannel.Sms);
        }

        foreach (var key in NotificationTemplateKeys.ParcelEvents)
        {
            var whatsApp = Assert.Single(DefaultTemplates.All, s => s.Key == key && s.Channel == NotificationChannel.WhatsApp);
            Assert.Matches("^[a-z0-9_]+$", whatsApp.ProviderTemplateName!);
        }

        // A one-time code is never sent on WhatsApp.
        Assert.DoesNotContain(DefaultTemplates.All, s => NotificationTemplateKeys.IsOneTimeCode(s.Key) && s.Channel == NotificationChannel.WhatsApp);
    }

    [Fact]
    public void TheSuggestedWhatsAppName_IsLowercaseWithUnderscores()
    {
        Assert.Equal("afno_parcel_out_for_delivery", DefaultTemplates.WhatsAppTemplateName("parcel-out-for-delivery"));
    }

    [Theory]
    [InlineData(ParcelStatusCode.BookingCreated, "parcel-booked")]
    [InlineData(ParcelStatusCode.PickedUp, "parcel-picked-up")]
    [InlineData(ParcelStatusCode.Shipped, "parcel-shipped")]
    [InlineData(ParcelStatusCode.InTransit, "parcel-in-transit")]
    [InlineData(ParcelStatusCode.ArrivedAtWarehouse, "parcel-at-warehouse")]
    [InlineData(ParcelStatusCode.OutForDelivery, "parcel-out-for-delivery")]
    [InlineData(ParcelStatusCode.Delivered, "parcel-delivered")]
    [InlineData(ParcelStatusCode.DeliveryFailed, "parcel-delivery-failed")]
    [InlineData(ParcelStatusCode.Returned, "parcel-returned")]
    public void EveryEligibleStatusChange_HasItsOwnMessage(ParcelStatusCode status, string key)
    {
        Assert.Equal(key, NotificationEvents.TemplateFor(status));
    }

    [Fact]
    public void ACancelledBooking_SendsNothing()
    {
        Assert.Null(NotificationEvents.TemplateFor(ParcelStatusCode.Cancelled));
    }
}

public sealed class NotificationEligibilityTests
{
    private static EligibilityInput Input(
        NotificationAudience audience = NotificationAudience.Sender,
        NotificationChannel channel = NotificationChannel.Sms,
        bool mobile = true,
        bool optedOut = false,
        bool? consent = true,
        bool notifyReceivers = true,
        bool template = true,
        bool named = true) =>
        new(audience, channel, mobile, optedOut, consent, notifyReceivers, template, named);

    [Fact]
    public void ASenderWhoAgreed_IsMessaged()
    {
        Assert.True(NotificationEligibility.Decide(Input()).Send);
    }

    [Fact]
    public void SilenceIsNotConsent_ForASender()
    {
        var decision = NotificationEligibility.Decide(Input(consent: null));

        Assert.False(decision.Send);
        Assert.Contains("not agreed", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASenderWhoWithdrewConsent_IsNotMessaged()
    {
        var decision = NotificationEligibility.Decide(Input(consent: false));

        Assert.False(decision.Send);
        Assert.Contains("withdrawn", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptOut_BeatsConsent()
    {
        var decision = NotificationEligibility.Decide(Input(consent: true, optedOut: true));

        Assert.False(decision.Send);
        Assert.Contains("opted out", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AReceiver_IsMessagedWithoutConsent_UnlessTheyOptedOut()
    {
        Assert.True(NotificationEligibility.Decide(Input(NotificationAudience.Receiver, consent: null)).Send);
        Assert.False(NotificationEligibility.Decide(Input(NotificationAudience.Receiver, consent: null, optedOut: true)).Send);
    }

    [Fact]
    public void TheOperatorCanSwitchReceiverMessagesOff_ForAChannel()
    {
        var decision = NotificationEligibility.Decide(Input(NotificationAudience.Receiver, notifyReceivers: false));

        Assert.False(decision.Send);
        Assert.Contains("receivers", decision.Reason, StringComparison.Ordinal);
        Assert.True(NotificationEligibility.Decide(Input(NotificationAudience.Sender, notifyReceivers: false)).Send);
    }

    [Fact]
    public void ALandline_CannotReceiveMessages()
    {
        Assert.False(NotificationEligibility.Decide(Input(mobile: false)).Send);
    }

    [Fact]
    public void NoTemplate_MeansNoMessage_AndAnUnnamedWhatsAppTemplateIsNotSent()
    {
        Assert.False(NotificationEligibility.Decide(Input(template: false)).Send);

        var unnamed = NotificationEligibility.Decide(Input(channel: NotificationChannel.WhatsApp, named: false));
        Assert.False(unnamed.Send);
        Assert.Contains("approved template name", unnamed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLatestConsentDecision_IsTheCurrentOne()
    {
        var customer = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var history = new[]
        {
            CustomerConsent.Record(customer, ConsentType.SmsNotifications, true, "1.0", ConsentSource.Counter, t0),
            CustomerConsent.Record(customer, ConsentType.SmsNotifications, false, "1.0", ConsentSource.Web, t0.AddDays(1)),
            CustomerConsent.Record(customer, ConsentType.WhatsAppNotifications, true, "1.0", ConsentSource.Web, t0),
        };

        Assert.False(NotificationEligibility.CurrentConsent(history, ConsentType.SmsNotifications));
        Assert.True(NotificationEligibility.CurrentConsent(history, ConsentType.WhatsAppNotifications));
        Assert.Null(NotificationEligibility.CurrentConsent(history, ConsentType.Marketing));
    }

    [Theory]
    [InlineData("9841234567", true)]
    [InlineData("9612345678", true)]
    [InlineData("014123456", false)]
    [InlineData("", false)]
    public void OnlyMobilesAreReachable(string phone, bool expected)
    {
        Assert.Equal(expected, NotificationEligibility.IsMobile(phone));
    }

    [Fact]
    public void ProvidersAreGivenTheNumberWithTheCountryCode()
    {
        Assert.Equal("9779841234567", NotificationEligibility.ToInternational("9841234567"));
    }
}

public sealed class RetryBackoffTests
{
    private static readonly OutboxOptions Options = new() { BaseDelaySeconds = 30, MaxDelaySeconds = 3600 };

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    public void TheDelayDoubles_WithEachFailure(int attempts, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RetryBackoff.Delay(attempts, Options, jitter: 0.5));
    }

    [Fact]
    public void TheDelayStopsGrowing_AtTheCap()
    {
        Assert.Equal(TimeSpan.FromSeconds(3600), RetryBackoff.Delay(30, Options, jitter: 0.5));
        Assert.True(RetryBackoff.Delay(30, Options, jitter: 1) <= TimeSpan.FromSeconds(3600));
    }

    [Fact]
    public void Jitter_MovesTheDelayByAtMostTwentyPercent_AndNeverBelowTheBase()
    {
        var low = RetryBackoff.Delay(3, Options, jitter: 0);
        var high = RetryBackoff.Delay(3, Options, jitter: 1);

        Assert.Equal(TimeSpan.FromSeconds(96), low);
        Assert.Equal(TimeSpan.FromSeconds(144), high);
    }

    [Fact]
    public void TheRandomOverload_StaysWithinTheJitterRange()
    {
        for (var i = 0; i < 50; i++)
        {
            var delay = RetryBackoff.Delay(2, Options);
            Assert.InRange(delay.TotalSeconds, 48, 72);
        }
    }
}

public sealed class NotificationPlannerTests
{
    private static readonly DateTimeOffset Now = TestData.Now;

    private readonly INotificationOutboxRepository _outbox = Substitute.For<INotificationOutboxRepository>();
    private readonly INotificationTemplateRepository _templates = Substitute.For<INotificationTemplateRepository>();
    private readonly INotificationOptOutRepository _optOuts = Substitute.For<INotificationOptOutRepository>();
    private readonly INotificationConsentRepository _consents = Substitute.For<INotificationConsentRepository>();
    private readonly NotificationOptions _options = new();
    private readonly List<NotificationOutboxMessage> _added = [];

    public NotificationPlannerTests()
    {
        _outbox.When(o => o.Add(Arg.Any<NotificationOutboxMessage>())).Do(c => _added.Add(c.Arg<NotificationOutboxMessage>()));
        _optOuts.GetActiveAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns([]);
        _consents.GetChannelConsentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        _templates.FindActiveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => DefaultTemplates.All
                .Where(s => s.Key == call.ArgAt<string>(0))
                .Select(s => NotificationTemplate.Create(s.Key, s.Channel, "en", s.Body, s.ProviderTemplateName, Now))
                .ToList());
    }

    private NotificationPlanner Planner() =>
        new(_outbox, _templates, _optOuts, _consents, Options.Create(_options), Options.Create(new TrackingOptions { PublicBaseUrl = "https://afno.test/" }), new FixedClock(Now));

    private static IReadOnlyList<CustomerConsent> Consent(Guid customer, bool sms, bool whatsApp) =>
    [
        CustomerConsent.Record(customer, ConsentType.SmsNotifications, sms, "1.0", ConsentSource.Counter, Now.AddDays(-1)),
        CustomerConsent.Record(customer, ConsentType.WhatsAppNotifications, whatsApp, "1.0", ConsentSource.Counter, Now.AddDays(-1)),
    ];

    private NotificationOutboxMessage Find(NotificationAudience audience, NotificationChannel channel) =>
        Assert.Single(_added, m => m.Audience == audience && m.Channel == channel);

    [Fact]
    public async Task ABooking_QueuesForEveryoneWhoMayBeMessaged_AndRecordsWhoWasSkippedAndWhy()
    {
        var parcel = TestData.NewParcel();
        _consents.GetChannelConsentsAsync(parcel.SenderCustomerId, Arg.Any<CancellationToken>()).Returns(Consent(parcel.SenderCustomerId, sms: true, whatsApp: false));

        await Planner().PlanAsync(parcel, parcel.StatusHistory.First(), null);

        Assert.Equal(4, _added.Count);
        Assert.Equal(OutboxStatus.Pending, Find(NotificationAudience.Sender, NotificationChannel.Sms).Status);
        var skipped = Find(NotificationAudience.Sender, NotificationChannel.WhatsApp);
        Assert.Equal(OutboxStatus.Skipped, skipped.Status);
        Assert.Contains("withdrawn", skipped.SkipReason, StringComparison.Ordinal);
        Assert.Equal(OutboxStatus.Pending, Find(NotificationAudience.Receiver, NotificationChannel.Sms).Status);
        Assert.Equal(OutboxStatus.Pending, Find(NotificationAudience.Receiver, NotificationChannel.WhatsApp).Status);
    }

    [Fact]
    public async Task Messages_AreAddressedToTheNumbersOnTheParcel_WithTheTrackingNumberAndLink()
    {
        var parcel = TestData.NewParcel();

        await Planner().PlanAsync(parcel, parcel.StatusHistory.First(), null);

        var toReceiver = Find(NotificationAudience.Receiver, NotificationChannel.Sms);
        Assert.Equal("9851234567", toReceiver.Recipient);
        Assert.Equal(NotificationTemplateKeys.ParcelBooked, toReceiver.TemplateKey);
        var values = TemplateRenderer.ValuesFromPayload(toReceiver.PayloadJson);
        Assert.Equal(parcel.TrackingNumber, values["tracking"]);
        Assert.Equal($"https://afno.test/track/{parcel.TrackingNumber}", values["link"]);
        Assert.Equal("Standard", values["service"]);
        Assert.DoesNotContain("Sita", toReceiver.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("9851234567", toReceiver.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(parcel.Id, toReceiver.ParcelId);
    }

    [Fact]
    public async Task EachMessage_IsKeyedByTheStatusChangeThatCausedIt()
    {
        var parcel = TestData.NewParcel();
        var history = parcel.ChangeStatus(ParcelStatusCode.PickedUp, Now.AddHours(1), null, "t", "Collected", parcel.OriginBranchId);

        await Planner().PlanAsync(parcel, history, null);

        Assert.All(_added, m => Assert.Equal(history.Id.ToString("N"), m.DedupeKey));
        Assert.All(_added, m => Assert.Equal(NotificationTemplateKeys.ParcelPickedUp, m.TemplateKey));
    }

    [Fact]
    public async Task ASenderWithNoConsentRecorded_IsNotMessaged_ButAReceiverIs()
    {
        var parcel = TestData.NewParcel();

        await Planner().PlanAsync(parcel, parcel.StatusHistory.First(), null);

        Assert.Equal(OutboxStatus.Skipped, Find(NotificationAudience.Sender, NotificationChannel.Sms).Status);
        Assert.Contains("not agreed", Find(NotificationAudience.Sender, NotificationChannel.Sms).SkipReason, StringComparison.Ordinal);
        Assert.Equal(OutboxStatus.Pending, Find(NotificationAudience.Receiver, NotificationChannel.Sms).Status);
    }

    [Fact]
    public async Task ConsentThatIsNotSavedYet_IsUsedInsteadOfTheDatabase()
    {
        var parcel = TestData.NewParcel();
        var fresh = Consent(parcel.SenderCustomerId, sms: true, whatsApp: true);

        await Planner().PlanAsync(parcel, parcel.StatusHistory.First(), [.. fresh]);

        await _consents.DidNotReceive().GetChannelConsentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        Assert.Equal(OutboxStatus.Pending, Find(NotificationAudience.Sender, NotificationChannel.Sms).Status);
        Assert.Equal(OutboxStatus.Pending, Find(NotificationAudience.Sender, NotificationChannel.WhatsApp).Status);
    }

    [Fact]
    public async Task AnOptedOutNumber_IsSkipped_OnThatChannelOnly()
    {
        var parcel = TestData.NewParcel();
        _optOuts.GetActiveAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([NotificationOptOut.Create("9851234567", NotificationChannel.Sms, OptOutSource.InboundMessage, Now)]);

        await Planner().PlanAsync(parcel, parcel.StatusHistory.First(), [.. Consent(parcel.SenderCustomerId, true, true)]);

        var sms = Find(NotificationAudience.Receiver, NotificationChannel.Sms);
        Assert.Equal(OutboxStatus.Skipped, sms.Status);
        Assert.Contains("opted out", sms.SkipReason, StringComparison.Ordinal);
        Assert.Equal(OutboxStatus.Pending, Find(NotificationAudience.Receiver, NotificationChannel.WhatsApp).Status);
        Assert.Equal(OutboxStatus.Pending, Find(NotificationAudience.Sender, NotificationChannel.Sms).Status);
    }

    [Fact]
    public async Task ASenderWhoIsAlsoTheReceiver_GetsOneMessagePerChannel_NotTwo()
    {
        var details = TestData.ParcelDetails();
        var parcel = Parcel.Book(
            new NewParcelDetails
            {
                TrackingNumber = details.TrackingNumber,
                SenderCustomerId = details.SenderCustomerId,
                SenderName = details.SenderName,
                SenderPhone = "9841234567",
                SenderAddress = details.SenderAddress,
                ReceiverName = "Ram Again",
                ReceiverPhone = "+977 984-1234567",
                ReceiverAddress = details.ReceiverAddress,
                OriginBranchId = details.OriginBranchId,
                ServiceType = details.ServiceType,
                Channel = details.Channel,
                Description = details.Description,
                Quantity = details.Quantity,
                WeightKg = details.WeightKg,
                PaymentMethod = details.PaymentMethod,
                Price = details.Price,
            },
            Now,
            bookedByName: "t");

        await Planner().PlanAsync(parcel, parcel.StatusHistory.First(), [.. Consent(parcel.SenderCustomerId, true, true)]);

        Assert.Equal(2, _added.Count);
        Assert.All(_added, m => Assert.Equal(NotificationAudience.Sender, m.Audience));
    }

    [Fact]
    public async Task ReceiverMessages_CanBeSwitchedOff_PerChannel()
    {
        var parcel = TestData.NewParcel();
        _options.WhatsApp.NotifyReceivers = false;

        await Planner().PlanAsync(parcel, parcel.StatusHistory.First(), [.. Consent(parcel.SenderCustomerId, true, true)]);

        Assert.Equal(OutboxStatus.Pending, Find(NotificationAudience.Receiver, NotificationChannel.Sms).Status);
        Assert.Equal(OutboxStatus.Skipped, Find(NotificationAudience.Receiver, NotificationChannel.WhatsApp).Status);
    }

    [Fact]
    public async Task WithNotificationsSwitchedOff_NothingIsQueued()
    {
        var parcel = TestData.NewParcel();
        _options.Enabled = false;

        await Planner().PlanAsync(parcel, parcel.StatusHistory.First(), null);

        Assert.Empty(_added);
    }

    [Fact]
    public async Task ACancelledBooking_QueuesNothing()
    {
        var parcel = TestData.NewParcel();
        var history = parcel.Cancel("Changed my mind", byStaff: false, Now.AddMinutes(5), null, "customer");

        await Planner().PlanAsync(parcel, history, null);

        Assert.Empty(_added);
    }

    [Fact]
    public async Task AMissingWhatsAppTemplate_IsRecordedAsSkipped_NotSilentlyIgnored()
    {
        var parcel = TestData.NewParcel();
        _templates.FindActiveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([NotificationTemplate.Create(NotificationTemplateKeys.ParcelBooked, NotificationChannel.Sms, "en", "Booked {tracking}", null, Now)]);

        await Planner().PlanAsync(parcel, parcel.StatusHistory.First(), [.. Consent(parcel.SenderCustomerId, true, true)]);

        var whatsApp = Find(NotificationAudience.Receiver, NotificationChannel.WhatsApp);
        Assert.Equal(OutboxStatus.Skipped, whatsApp.Status);
        Assert.Contains("template", whatsApp.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALandlineReceiver_IsSkippedForBothChannels()
    {
        var details = TestData.ParcelDetails();
        var parcel = Parcel.Book(
            new NewParcelDetails
            {
                TrackingNumber = details.TrackingNumber,
                SenderCustomerId = details.SenderCustomerId,
                SenderName = details.SenderName,
                SenderPhone = details.SenderPhone,
                SenderAddress = details.SenderAddress,
                ReceiverName = details.ReceiverName,
                ReceiverPhone = "01-4123456",
                ReceiverAddress = details.ReceiverAddress,
                OriginBranchId = details.OriginBranchId,
                ServiceType = details.ServiceType,
                Channel = details.Channel,
                Description = details.Description,
                Quantity = details.Quantity,
                WeightKg = details.WeightKg,
                PaymentMethod = details.PaymentMethod,
                Price = details.Price,
            },
            Now,
            bookedByName: "t");

        await Planner().PlanAsync(parcel, parcel.StatusHistory.First(), [.. Consent(parcel.SenderCustomerId, true, true)]);

        Assert.All(
            _added.Where(m => m.Audience == NotificationAudience.Receiver),
            m =>
            {
                Assert.Equal(OutboxStatus.Skipped, m.Status);
                Assert.Contains("mobile", m.SkipReason, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task AWalkThroughTheWholeJourney_QueuesTheRightMessageForEachStep()
    {
        var parcel = TestData.NewParcel();
        var warehouse = Guid.NewGuid();
        var steps = new (ParcelStatusCode Status, string Key)[]
        {
            (ParcelStatusCode.PickedUp, "parcel-picked-up"),
            (ParcelStatusCode.Shipped, "parcel-shipped"),
            (ParcelStatusCode.InTransit, "parcel-in-transit"),
            (ParcelStatusCode.ArrivedAtWarehouse, "parcel-at-warehouse"),
            (ParcelStatusCode.OutForDelivery, "parcel-out-for-delivery"),
            (ParcelStatusCode.DeliveryFailed, "parcel-delivery-failed"),
            (ParcelStatusCode.OutForDelivery, "parcel-out-for-delivery"),
            (ParcelStatusCode.Delivered, "parcel-delivered"),
        };

        var at = Now;
        foreach (var (status, key) in steps)
        {
            at = at.AddHours(1);
            _added.Clear();
            var rule = ParcelStatusRules.LocationFor(status);
            var history = parcel.ChangeStatus(
                status,
                at,
                null,
                "t",
                ParcelStatusRules.RequiresRemarks(status) ? "remark" : null,
                rule is LocationRule.Branch or LocationRule.BranchOrWarehouse ? parcel.OriginBranchId : null,
                rule == LocationRule.Warehouse ? warehouse : null);

            await Planner().PlanAsync(parcel, history, null);

            Assert.Equal(4, _added.Count);
            Assert.All(_added, m => Assert.Equal(key, m.TemplateKey));
        }
    }
}

public sealed class NotificationOptionsTests
{
    [Fact]
    public void EveryMetaSecretIsRequired_OnlyWhenMetaIsChosen()
    {
        var validator = new NotificationOptionsValidator();

        Assert.True(validator.Validate(null, new NotificationOptions()).Succeeded);

        var incomplete = validator.Validate(null, new NotificationOptions { WhatsApp = { Provider = "Meta" } });
        Assert.True(incomplete.Failed);
        Assert.Equal(4, incomplete.Failures!.Count());
        Assert.All(incomplete.Failures!, f => Assert.Contains("user-secrets", f, StringComparison.Ordinal));

        Assert.True(validator.Validate(null, new NotificationOptions { WhatsApp = { Provider = "Meta", PhoneNumberId = "1", AccessToken = "t", AppSecret = "s", VerifyToken = "v" } }).Succeeded);
    }

    [Fact]
    public void TheLongestRetryDelay_CannotBeShorterThanTheFirst()
    {
        var validator = new NotificationOptionsValidator();

        Assert.True(validator.Validate(null, new NotificationOptions { Outbox = { BaseDelaySeconds = 100, MaxDelaySeconds = 60 } }).Failed);
    }

    [Fact]
    public void TheDefaults_UseMockProviders_AndCarryNoSecrets()
    {
        var options = new NotificationOptions();

        Assert.Equal("Mock", options.WhatsApp.Provider);
        Assert.Equal("Mock", options.Sms.Provider);
        Assert.Null(options.WhatsApp.AccessToken);
        Assert.Null(options.WhatsApp.AppSecret);
        Assert.Null(options.WhatsApp.VerifyToken);
        Assert.Null(options.WhatsApp.PhoneNumberId);
        Assert.False(options.WhatsApp.IsMeta);
    }
}

public sealed class MessageProviderRegistryTests
{
    private sealed class FakeProvider(NotificationChannel channel, string name, string configurationName, bool isMock, bool configured = true) : IMessageProvider
    {
        public NotificationChannel Channel => channel;

        public string Name => name;

        public string ConfigurationName => configurationName;

        public bool IsMock => isMock;

        public bool IsConfigured => configured;

        public string Description => "fake";

        public Task<SendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default) => Task.FromResult(SendResult.Accepted("x"));
    }

    private static MessageProviderRegistry Registry(NotificationOptions options, params IMessageProvider[] providers) =>
        new(providers, Options.Create(options));

    [Fact]
    public void TheProviderNamedInConfigurationIsUsed_PerChannel()
    {
        var mockWa = new FakeProvider(NotificationChannel.WhatsApp, "MockWhatsApp", "Mock", true);
        var metaWa = new FakeProvider(NotificationChannel.WhatsApp, "MetaWhatsApp", "Meta", false);
        var mockSms = new FakeProvider(NotificationChannel.Sms, "MockSms", "Mock", true);

        var useMock = Registry(new NotificationOptions(), mockWa, metaWa, mockSms);
        var useMeta = Registry(new NotificationOptions { WhatsApp = { Provider = "Meta" } }, mockWa, metaWa, mockSms);

        Assert.Same(mockWa, useMock.Get(NotificationChannel.WhatsApp));
        Assert.Same(metaWa, useMeta.Get(NotificationChannel.WhatsApp));
        Assert.Same(mockSms, useMeta.Get(NotificationChannel.Sms));
        Assert.True(useMock.IsMockInUse);
        Assert.True(useMeta.IsMockInUse);
    }

    [Fact]
    public void AnUnknownProviderName_IsReportedPlainly_NotSilentlyReplacedByTheMock()
    {
        var registry = Registry(
            new NotificationOptions { Sms = { Provider = "SparrowSms" } },
            new FakeProvider(NotificationChannel.Sms, "MockSms", "Mock", true),
            new FakeProvider(NotificationChannel.WhatsApp, "MockWhatsApp", "Mock", true));

        var error = Assert.Throws<DomainException>(() => registry.Get(NotificationChannel.Sms));
        Assert.Contains("SparrowSms", error.Message, StringComparison.Ordinal);

        var info = Assert.Single(registry.Describe(), i => i.Channel == "Sms");
        Assert.False(info.IsConfigured);
        Assert.False(info.IsMock);
        Assert.Equal("SparrowSms", info.Provider);
    }

    [Fact]
    public void WithOnlyRealProviders_NoMockIsInUse_AndTheDescriptionNeverContainsCredentials()
    {
        var registry = Registry(
            new NotificationOptions { WhatsApp = { Provider = "Meta", AccessToken = "TOP-SECRET-TOKEN" } },
            new FakeProvider(NotificationChannel.WhatsApp, "MetaWhatsApp", "Meta", false, configured: false),
            new FakeProvider(NotificationChannel.Sms, "RealSms", "Mock", false));

        Assert.False(registry.IsMockInUse);
        var whatsApp = Assert.Single(registry.Describe(), i => i.Channel == "WhatsApp");
        Assert.False(whatsApp.IsConfigured);
        Assert.Contains("credentials", whatsApp.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET-TOKEN", string.Join(" ", registry.Describe().Select(i => i.Detail)), StringComparison.Ordinal);
    }
}
