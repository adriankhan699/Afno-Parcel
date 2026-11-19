using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Delivery;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Payments;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Payments;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Payments;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NepalParcel.UnitTests.Application;

/// <summary>
/// What the server decides after the provider answers. The gateway here is a fake that says whatever each test needs, so these
/// tests show that "completed" is never enough on its own: the reference, amount, currency and provider id must all be right.
/// </summary>
public sealed class PaymentVerificationTests
{
    private const decimal Charge = 250.00m;

    private readonly NetworkSetup _net = new();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly IPaymentGatewayRegistry _registry = Substitute.For<IPaymentGatewayRegistry>();
    private readonly IPaymentRepository _payments = Substitute.For<IPaymentRepository>();
    private readonly IPaymentTransactionRepository _transactions = Substitute.For<IPaymentTransactionRepository>();
    private readonly IParcelRepository _parcels = Substitute.For<IParcelRepository>();
    private readonly NepalParcel.Application.Auth.ICustomerRepository _customers = Substitute.For<NepalParcel.Application.Auth.ICustomerRepository>();
    private readonly Payment _payment;
    private readonly PaymentTransaction _transaction;
    private readonly PaymentService _sut;

    public PaymentVerificationTests()
    {
        _payment = Payment.Create(Guid.NewGuid(), Charge, PaymentMethod.Esewa);
        _transaction = PaymentTransaction.Create(
            _payment.Id,
            TransactionType.Charge,
            "Esewa",
            Charge,
            TestData.Now,
            expiresAtUtc: TestData.Now.AddMinutes(30));

        _gateway.Provider.Returns("Esewa");
        _registry.Get("Esewa").Returns(_gateway);
        _transactions.GetByMerchantReferenceAsync(_transaction.MerchantReference, Arg.Any<CancellationToken>()).Returns(_transaction);
        _payments.GetByIdAsync(_payment.Id, Arg.Any<CancellationToken>()).Returns(_payment);

        _sut = new PaymentService(
            _registry,
            _payments,
            _transactions,
            _parcels,
            _customers,
            new PaymentLedger(_transactions),
            _net.Access,
            _net.Audit,
            _net.UnitOfWork,
            _net.CurrentUser,
            _net.Client,
            new FixedClock(TestData.Now.AddMinutes(5)),
            Options.Create(new PaymentOptions()),
            Options.Create(new BookingOptions()),
            new InitiatePaymentRequestValidator(),
            new PaymentCallbackRequestValidator(),
            new RecordOfficeCashRequestValidator(),
            new PaymentTransactionQueryValidator(),
            NullLogger<PaymentService>.Instance);
    }

    private void ProviderSays(GatewayState state, decimal? amount = Charge, string? currency = "NPR", string? providerId = "PROV-1", string? reference = null) =>
        _gateway
            .VerifyAsync(Arg.Any<GatewayVerifyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayVerification(state, reference ?? _transaction.MerchantReference, amount, currency, providerId, state == GatewayState.Failed ? "declined" : null));

    private Task<PaymentStatusResponse> VerifyAsync(Dictionary<string, string>? data = null) =>
        _sut.VerifyAsync("Esewa", new PaymentCallbackRequest(_transaction.MerchantReference, data));

    [Fact]
    public async Task ACompletedPayment_ThatMatchesEverything_IsRecordedAndMarkedPaid()
    {
        ProviderSays(GatewayState.Completed);

        var result = await VerifyAsync();

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal("Paid", result.PaymentStatus);
        Assert.Equal(TransactionStatus.Succeeded, _transaction.Status);
        Assert.Equal("PROV-1", _transaction.ProviderReference);
        Assert.Equal(PaymentStatus.Paid, _payment.Status);
        Assert.Equal("PROV-1", _payment.Reference);
        _net.Audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.PaymentVerified));
        await _net.UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("reference", "the merchant reference does not match")]
    [InlineData("amount", "the amount does not match")]
    [InlineData("currency", "the currency does not match")]
    [InlineData("noprovider", "the provider gave no transaction reference")]
    [InlineData("reused", "already used by another payment")]
    public async Task ACompletedPayment_ThatDoesNotMatch_IsRefused_AndNothingIsPaid(string problem, string expected)
    {
        switch (problem)
        {
            case "reference":
                ProviderSays(GatewayState.Completed, reference: "AFPSOMEONEELSESPAYMENT");
                break;
            case "amount":
                ProviderSays(GatewayState.Completed, amount: Charge - 0.01m);
                break;
            case "currency":
                ProviderSays(GatewayState.Completed, currency: "INR");
                break;
            case "noprovider":
                ProviderSays(GatewayState.Completed, providerId: " ");
                break;
            default:
                ProviderSays(GatewayState.Completed);
                _transactions.ProviderReferenceUsedAsync("Esewa", "PROV-1", _transaction.Id, Arg.Any<CancellationToken>()).Returns(true);
                break;
        }

        var result = await VerifyAsync();

        Assert.Equal("Failed", result.Status);
        Assert.Contains(expected, result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(PaymentStatus.Pending, _payment.Status);
        Assert.Null(_transaction.ProviderReference);
        _net.Audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.PaymentVerificationMismatch));
        _net.Audit.DidNotReceive().Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.PaymentVerified));
    }

    [Fact]
    public async Task WhatTheBrowserSent_IsNeverTheAnswer()
    {
        ProviderSays(GatewayState.Pending);

        var result = await VerifyAsync(new Dictionary<string, string> { ["status"] = "COMPLETE", ["amount"] = "250", ["transaction_code"] = "FORGED" });

        Assert.Equal("Pending", result.Status);
        Assert.Equal(PaymentStatus.Pending, _payment.Status);
        Assert.Null(_transaction.ProviderReference);
        await _gateway.Received(1).VerifyAsync(
            Arg.Is<GatewayVerifyRequest>(r =>
                r.MerchantReference == _transaction.MerchantReference
                && r.ExpectedAmount == Charge
                && r.ExpectedCurrency == "NPR"
                && r.CallbackData["transaction_code"] == "FORGED"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APaymentTheProviderHasNotCompleted_StaysOpen_UntilItRunsOutOfTime()
    {
        ProviderSays(GatewayState.Pending);
        var open = await VerifyAsync();
        Assert.Equal("Pending", open.Status);

        var late = new PaymentService(
            _registry, _payments, _transactions, _parcels, _customers, new PaymentLedger(_transactions), _net.Access, _net.Audit, _net.UnitOfWork,
            _net.CurrentUser, _net.Client, new FixedClock(TestData.Now.AddMinutes(31)), Options.Create(new PaymentOptions()), Options.Create(new BookingOptions()),
            new InitiatePaymentRequestValidator(), new PaymentCallbackRequestValidator(), new RecordOfficeCashRequestValidator(), new PaymentTransactionQueryValidator(),
            NullLogger<PaymentService>.Instance);

        var expired = await late.VerifyAsync("Esewa", new PaymentCallbackRequest(_transaction.MerchantReference));

        Assert.Equal("Failed", expired.Status);
        Assert.Contains("in time", expired.FailureReason, StringComparison.Ordinal);
        Assert.Equal(PaymentStatus.Pending, _payment.Status);
    }

    [Theory]
    [InlineData(GatewayState.Failed)]
    [InlineData(GatewayState.Cancelled)]
    public async Task AFailedOrCancelledPayment_FailsTheAttempt_ButNotTheCharge(GatewayState state)
    {
        ProviderSays(state, amount: null, providerId: null);

        var result = await VerifyAsync();

        Assert.Equal("Failed", result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
        Assert.Equal("Pending", result.PaymentStatus);
        _net.Audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.PaymentFailed));
    }

    [Fact]
    public async Task AFinishedAttempt_IsFinal_AndTheProviderIsNotAskedAgain()
    {
        ProviderSays(GatewayState.Completed);
        await VerifyAsync();
        _gateway.ClearReceivedCalls();
        _net.Audit.ClearReceivedCalls();

        var again = await VerifyAsync();
        var third = await VerifyAsync();

        Assert.Equal("Succeeded", again.Status);
        Assert.Equal("Succeeded", third.Status);
        await _gateway.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);
        _net.Audit.DidNotReceive().Add(Arg.Any<AuditLog>());
        Assert.Equal(PaymentStatus.Paid, _payment.Status);
    }

    [Fact]
    public async Task MoneyTakenForAChargeThatWasAlreadyPaid_IsRecorded_ButNotPaidTwice()
    {
        _payment.MarkPaid(TestData.Now, "CASH");
        var paidAt = _payment.PaidAtUtc;
        ProviderSays(GatewayState.Completed);

        var result = await VerifyAsync();

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(PaymentStatus.Paid, _payment.Status);
        Assert.Equal("CASH", _payment.Reference);
        Assert.Equal(paidAt, _payment.PaidAtUtc);
        _net.Audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.PaymentDuplicateCharge));
        _net.Audit.DidNotReceive().Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.PaymentVerified));
    }

    [Fact]
    public async Task WhenAnotherRequestFinishesTheSameAttemptFirst_ItsDecisionIsReported()
    {
        ProviderSays(GatewayState.Completed);
        _net.UnitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new ConcurrencyException());

        var result = await VerifyAsync();

        _net.UnitOfWork.Received(1).DiscardPendingChanges();
        Assert.Equal(_transaction.MerchantReference, result.Reference);
    }

    [Fact]
    public async Task AReferenceOfAnotherProviderOrThatDoesNotExist_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.VerifyAsync("Khalti", new PaymentCallbackRequest(_transaction.MerchantReference)));
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.VerifyAsync("Esewa", new PaymentCallbackRequest(MerchantReferences.New())));
        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.VerifyAsync("Esewa", new PaymentCallbackRequest("nonsense")));
        await _gateway.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);
    }

    [Fact]
    public async Task WhenNoGatewayIsAvailable_VerificationIsRefused_AndNothingChanges()
    {
        _registry.Get("Esewa").Throws(new DomainException("Online payment with Esewa is not available right now."));

        await Assert.ThrowsAsync<DomainException>(() => VerifyAsync());

        Assert.Equal(TransactionStatus.Pending, _transaction.Status);
        Assert.Equal(PaymentStatus.Pending, _payment.Status);
    }
}

public sealed class PaymentGatewayTests
{
    private sealed class InMemorySessions : ITestGatewaySessions
    {
        public List<TestGatewaySession> Items { get; } = [];

        public void Add(TestGatewaySession session) => Items.Add(session);

        public Task<TestGatewaySession?> GetByReferenceAsync(string merchantReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(s => s.MerchantReference == merchantReference));
    }

    private readonly InMemorySessions _sessions = new();
    private readonly IOptions<TrackingOptions> _tracking = Options.Create(new TrackingOptions { PublicBaseUrl = "https://afno.example/" });

    private PaymentGatewayRegistry Registry(string mode, params IPaymentGateway[] real) =>
        new(Options.Create(new PaymentOptions { Mode = mode }), real, _sessions, new FixedClock(TestData.Now), _tracking);

    [Fact]
    public async Task InTestMode_BothProvidersAreAvailable_AndOpenAnAttemptOnTheSimulatedPage()
    {
        var registry = Registry(PaymentOptions.TestMode);
        var reference = MerchantReferences.New();

        var initiation = await registry.Get("khalti").InitiateAsync(new GatewayInitiateRequest(reference, 100m, "NPR", "Delivery", "/pay/return/khalti"));

        Assert.True(registry.IsTestMode);
        Assert.True(registry.Get("Esewa").IsTest);
        Assert.Equal($"https://afno.example/pay/test/khalti/{reference}", initiation.RedirectUrl);
        var session = Assert.Single(_sessions.Items);
        Assert.Equal(100m, session.Amount);
        Assert.Equal("Khalti", session.Provider);
    }

    [Fact]
    public async Task InitiatingTheSameReferenceAgain_OpensNoSecondAttempt()
    {
        var gateway = Registry(PaymentOptions.TestMode).Get("Esewa");
        var request = new GatewayInitiateRequest(MerchantReferences.New(), 50m, "NPR", "Delivery", "/x");

        var first = await gateway.InitiateAsync(request);
        var second = await gateway.InitiateAsync(request);

        Assert.Equal(first.RedirectUrl, second.RedirectUrl);
        Assert.Single(_sessions.Items);
    }

    [Fact]
    public async Task InLiveMode_NoOnlinePaymentIsPossible_UntilARealGatewayIsRegistered()
    {
        var live = Registry(PaymentOptions.LiveMode);
        Assert.False(live.IsTestMode);
        Assert.Throws<DomainException>(() => live.Get("Esewa"));
        Assert.Throws<DomainException>(() => live.Get("Khalti"));

        var real = Substitute.For<IPaymentGateway>();
        real.Provider.Returns("Esewa");
        var withReal = Registry(PaymentOptions.LiveMode, real);
        Assert.Same(real, withReal.Get("esewa"));
        Assert.Throws<DomainException>(() => withReal.Get("Khalti"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheSimulatedProvider_AnswersFromItsOwnBooks_NeverFromTheRequest()
    {
        var gateway = Registry(PaymentOptions.TestMode).Get("Esewa");
        var reference = MerchantReferences.New();
        await gateway.InitiateAsync(new GatewayInitiateRequest(reference, 80m, "NPR", "Delivery", "/x"));
        var forged = new Dictionary<string, string> { ["status"] = "COMPLETE", ["amount"] = "80" };

        var before = await gateway.VerifyAsync(new GatewayVerifyRequest(reference, 80m, "NPR", forged));
        _sessions.Items[0].Complete(TestSessionStatus.Paid, TestData.Now, "TEST-ABC");
        var after = await gateway.VerifyAsync(new GatewayVerifyRequest(reference, 80m, "NPR", new Dictionary<string, string>()));

        Assert.Equal(GatewayState.Pending, before.State);
        Assert.Null(before.ProviderTransactionId);
        Assert.Equal(GatewayState.Completed, after.State);
        Assert.Equal("TEST-ABC", after.ProviderTransactionId);
        Assert.Equal(80m, after.Amount);
        Assert.Equal("NPR", after.Currency);
        Assert.Equal(reference, after.MerchantReference);
    }

    [Theory]
    [InlineData(TestSessionStatus.Failed, GatewayState.Failed)]
    [InlineData(TestSessionStatus.Cancelled, GatewayState.Cancelled)]
    public async Task FailedAndCancelledSessions_AreReportedAsSuch(TestSessionStatus status, GatewayState expected)
    {
        var gateway = Registry(PaymentOptions.TestMode).Get("Khalti");
        var reference = MerchantReferences.New();
        await gateway.InitiateAsync(new GatewayInitiateRequest(reference, 80m, "NPR", "Delivery", "/x"));
        _sessions.Items[0].Complete(status, TestData.Now, null);

        var result = await gateway.VerifyAsync(new GatewayVerifyRequest(reference, 80m, "NPR", new Dictionary<string, string>()));

        Assert.Equal(expected, result.State);
        Assert.Null(result.ProviderTransactionId);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Fact]
    public async Task AnUnknownReference_ForTheOtherProvider_OrNeverOpened_IsSimplyNotPaid()
    {
        var registry = Registry(PaymentOptions.TestMode);
        var reference = MerchantReferences.New();
        await registry.Get("Esewa").InitiateAsync(new GatewayInitiateRequest(reference, 80m, "NPR", "Delivery", "/x"));
        _sessions.Items[0].Complete(TestSessionStatus.Paid, TestData.Now, "TEST-1");

        var other = await registry.Get("Khalti").VerifyAsync(new GatewayVerifyRequest(reference, 80m, "NPR", new Dictionary<string, string>()));
        var unknown = await registry.Get("Esewa").VerifyAsync(new GatewayVerifyRequest(MerchantReferences.New(), 80m, "NPR", new Dictionary<string, string>()));

        Assert.Equal(GatewayState.Pending, other.State);
        Assert.Equal(GatewayState.Pending, unknown.State);
    }
}

public sealed class PaymentDomainTests
{
    [Fact]
    public void MerchantReferences_AreRandomAndRecognisable()
    {
        var references = Enumerable.Range(0, 200).Select(_ => MerchantReferences.New()).ToList();

        Assert.Equal(200, references.Distinct().Count());
        Assert.All(references, r =>
        {
            Assert.StartsWith("AFP", r, StringComparison.Ordinal);
            Assert.Equal(20, r.Length);
            Assert.True(MerchantReferences.LooksValid(r));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AFP")]
    [InlineData("XXX7K2M9Q4XB3T8WRC5H")]
    [InlineData("AFP7K2M9Q4XB3T8WRC5!")]
    [InlineData("AFP0K2M9Q4XB3T8WRC5H")]
    [InlineData("AFP7K2M9Q4XB3T8WRC5HH")]
    public void NothingElse_LooksLikeAReference(string? value) => Assert.False(MerchantReferences.LooksValid(value));

    [Fact]
    public void ATransaction_HasItsOwnReference_Currency_AndExpiry()
    {
        var paymentId = Guid.NewGuid();
        var tx = PaymentTransaction.Create(paymentId, TransactionType.Charge, "Khalti", 99.5m, TestData.Now, expiresAtUtc: TestData.Now.AddMinutes(30));
        var manual = PaymentTransaction.Create(paymentId, TransactionType.Charge, "Cash", 99.5m, TestData.Now, recordedByUserId: Guid.NewGuid());

        Assert.True(MerchantReferences.LooksValid(tx.MerchantReference));
        Assert.NotEqual(tx.MerchantReference, manual.MerchantReference);
        Assert.Equal("NPR", tx.Currency);
        Assert.False(tx.IsExpired(TestData.Now.AddMinutes(29)));
        Assert.True(tx.IsExpired(TestData.Now.AddMinutes(30)));
        Assert.False(manual.IsExpired(TestData.Now.AddYears(5))); // never expires: it was complete the moment it was recorded
        Assert.NotNull(manual.RecordedByUserId);
        Assert.Null(manual.ProviderReference);
    }

    [Fact]
    public void ATransaction_CanBeGivenAReferenceExplicitly_ButNotABlankOne()
    {
        var tx = PaymentTransaction.Create(Guid.NewGuid(), TransactionType.Charge, "Esewa", 10m, TestData.Now, merchantReference: "REF-FIXED");

        Assert.Equal("REF-FIXED", tx.MerchantReference);
        Assert.Throws<DomainException>(() => PaymentTransaction.Create(Guid.NewGuid(), TransactionType.Charge, "Esewa", 10m, TestData.Now, merchantReference: " "));
    }

    [Fact]
    public void AReceiptNumber_ExistsOnlyOncePaid_AndNeverChanges()
    {
        var payment = Payment.Create(Guid.NewGuid(), 100m, PaymentMethod.Cash);
        Assert.Null(payment.ReceiptNumber);

        payment.MarkPaid(TestData.Now);
        var number = payment.ReceiptNumber;

        Assert.Matches("^PR-20261117-[0-9A-F]{8}$", number);
        Assert.Equal(number, payment.ReceiptNumber);
    }

    [Fact]
    public void ATestSession_IsCompletedOnce_AndOnlyAPaidOneHasAProviderId()
    {
        var session = TestGatewaySession.Create("Esewa", MerchantReferences.New(), 120m, TestData.Now);

        Assert.Throws<DomainException>(() => session.Complete(TestSessionStatus.Pending, TestData.Now, null));
        Assert.Throws<DomainException>(() => session.Complete(TestSessionStatus.Paid, TestData.Now, " "));
        session.Complete(TestSessionStatus.Paid, TestData.Now.AddSeconds(5), "TEST-1");

        Assert.Equal(TestSessionStatus.Paid, session.Status);
        Assert.Equal("TEST-1", session.ProviderTransactionId);
        Assert.Throws<DomainException>(() => session.Complete(TestSessionStatus.Failed, TestData.Now, null));

        var failed = TestGatewaySession.Create("Khalti", MerchantReferences.New(), 120m, TestData.Now);
        failed.Complete(TestSessionStatus.Failed, TestData.Now, "IGNORED");
        Assert.Null(failed.ProviderTransactionId);
    }

    [Fact]
    public void PaymentOptions_DefaultToTestMode()
    {
        var options = new PaymentOptions();

        Assert.True(options.IsTest);
        Assert.Equal(30, options.TransactionLifetimeMinutes);
    }
}

public sealed class CodSettlementDomainTests
{
    private static CodSettlement Submitted(decimal total = 1500m)
    {
        var settlement = CodSettlement.Create("set-1", Guid.NewGuid(), total, 2, Guid.NewGuid());
        settlement.Submit(TestData.Now, "Handed in");
        return settlement;
    }

    [Fact]
    public void ACountThatMatches_VerifiesTheSettlement()
    {
        var settlement = Submitted();

        settlement.Verify(TestData.Now.AddHours(1), Guid.NewGuid(), 1500m, "Counted twice");

        Assert.Equal(SettlementStatus.Verified, settlement.Status);
        Assert.Equal(1500m, settlement.CountedAmount);
        Assert.Equal(0m, settlement.Variance);
        Assert.Equal("Counted twice", settlement.Remarks);
    }

    [Fact]
    public void ACountThatDiffers_CannotVerify_ItMustBeDisputed_WithAReason()
    {
        var settlement = Submitted();

        Assert.Throws<DomainException>(() => settlement.Verify(TestData.Now, Guid.NewGuid(), 1400m));
        Assert.Throws<DomainException>(() => settlement.Dispute(" ", 1400m));
        settlement.Dispute("Short by 100", 1400m, Guid.NewGuid());

        Assert.Equal(SettlementStatus.Disputed, settlement.Status);
        Assert.Equal(-100m, settlement.Variance);
        Assert.Equal("Short by 100", settlement.Remarks);
    }

    [Fact]
    public void ADisputedSettlement_CanBeResolved_KeepingTheVariance()
    {
        var settlement = Submitted();
        settlement.Dispute("Short by 100", 1400m);

        Assert.Throws<DomainException>(() => settlement.Resolve(TestData.Now, Guid.NewGuid(), " "));
        settlement.Resolve(TestData.Now.AddDays(1), Guid.NewGuid(), "Deducted from pay");

        Assert.Equal(SettlementStatus.Verified, settlement.Status);
        Assert.Equal(-100m, settlement.Variance);
        Assert.Equal("Deducted from pay", settlement.Remarks);
        Assert.NotNull(settlement.VerifiedAtUtc);
    }

    [Fact]
    public void AnythingThatIsNotInTheRightState_IsRefused()
    {
        var draft = CodSettlement.Create("set-2", Guid.NewGuid(), 100m, 1);
        Assert.Throws<DomainException>(() => draft.Verify(TestData.Now, Guid.NewGuid(), 100m));
        Assert.Throws<DomainException>(() => draft.Resolve(TestData.Now, Guid.NewGuid(), "no"));

        var verified = Submitted(100m);
        verified.Verify(TestData.Now, Guid.NewGuid(), 100m);
        Assert.Throws<DomainException>(() => verified.Dispute("late", 90m));
        Assert.Throws<DomainException>(() => verified.Resolve(TestData.Now, Guid.NewGuid(), "late"));
        Assert.Throws<DomainException>(() => verified.Submit(TestData.Now));
    }

    [Fact]
    public void CashStaysCollected_UntilTheCountIsAccepted()
    {
        var collection = CodCollection.Record(Guid.NewGuid(), Guid.NewGuid(), 500m, 500m, TestData.Now);
        Assert.True(collection.IsInHand);
        Assert.Throws<DomainException>(() => collection.MarkSettled()); // not in a settlement yet
        Assert.Throws<DomainException>(() => collection.MarkDisputed());

        collection.AttachToSettlement(Guid.NewGuid());
        Assert.True(collection.IsInSettlement);
        Assert.False(collection.IsInHand);

        collection.MarkDisputed();
        Assert.Equal(CodCollectionStatus.Disputed, collection.Status);
        collection.MarkSettled(); // a disputed count that is resolved
        Assert.Equal(CodCollectionStatus.Settled, collection.Status);
        Assert.Throws<DomainException>(() => collection.MarkDisputed());
        Assert.Throws<DomainException>(() => collection.AttachToSettlement(Guid.NewGuid()));
    }

    [Fact]
    public void TheCashPosition_SplitsCollectionsByWhereTheCashIs()
    {
        var agent = DeliveryAgent.Create(Guid.NewGuid(), Guid.NewGuid(), "Ram Rider", "9841000111");
        var settlement = Guid.NewGuid();
        CodRow Row(decimal amount, CodCollectionStatus status, Guid? settlementId, int day) =>
            new(agent.Id, amount, status, settlementId, TestData.Now.AddDays(day));
        var rows = new[]
        {
            Row(100m, CodCollectionStatus.Collected, null, 1),
            Row(200m, CodCollectionStatus.Collected, settlement, 2),
            Row(400m, CodCollectionStatus.Disputed, settlement, 3),
            Row(800m, CodCollectionStatus.Settled, settlement, 4),
        };

        var position = SettlementService.Position(agent, "Kathmandu Main", rows);

        Assert.Equal(1500m, position.Collected);
        Assert.Equal(100m, position.InHand);
        Assert.Equal(200m, position.InSettlement);
        Assert.Equal(400m, position.Disputed);
        Assert.Equal(800m, position.Settled);
        Assert.Equal(4, position.CollectionCount);
        Assert.Equal(TestData.Now.AddDays(4), position.LastCollectionAtUtc);
        Assert.Equal(position.Collected, position.InHand + position.InSettlement + position.Disputed + position.Settled);

        var empty = SettlementService.Position(agent, "Kathmandu Main", []);
        Assert.Equal(0m, empty.Collected);
        Assert.Null(empty.LastCollectionAtUtc);
    }
}

public sealed class PaymentValidatorTests
{
    [Theory]
    [InlineData("Esewa", true)]
    [InlineData("khalti", true)]
    [InlineData("Cash", false)]
    [InlineData("CashOnDelivery", false)]
    [InlineData("Visa", false)]
    [InlineData("", false)]
    public void OnlyEsewaAndKhaltiCanBeStartedOnline(string provider, bool valid) =>
        Assert.Equal(valid, new InitiatePaymentRequestValidator().Validate(new InitiatePaymentRequest(Guid.NewGuid(), provider)).IsValid);

    [Fact]
    public void TheCallbackReference_MustLookLikeOurs_AndTheDataIsBounded()
    {
        var validator = new PaymentCallbackRequestValidator();
        var reference = MerchantReferences.New();

        Assert.True(validator.Validate(new PaymentCallbackRequest(reference)).IsValid);
        Assert.False(validator.Validate(new PaymentCallbackRequest("junk")).IsValid);
        Assert.False(validator.Validate(new PaymentCallbackRequest(reference, Enumerable.Range(0, 21).ToDictionary(i => $"k{i}", _ => "v"))).IsValid);
        Assert.False(validator.Validate(new PaymentCallbackRequest(reference, new Dictionary<string, string> { ["k"] = new string('x', 501) })).IsValid);
        Assert.False(validator.Validate(new PaymentCallbackRequest(reference, new Dictionary<string, string> { [new string('k', 51)] = "v" })).IsValid);
    }

    [Theory]
    [InlineData("Success", true)]
    [InlineData("failure", true)]
    [InlineData("CANCEL", true)]
    [InlineData("Maybe", false)]
    [InlineData("", false)]
    public void TheTesterCanSucceedFailOrCancel(string outcome, bool valid) =>
        Assert.Equal(valid, new TestGatewayCompleteRequestValidator().Validate(new TestGatewayCompleteRequest(outcome)).IsValid);

    [Theory]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    public void OfficeCash_MustBePositive(double amount, bool valid) =>
        Assert.Equal(valid, new RecordOfficeCashRequestValidator().Validate(new RecordOfficeCashRequest(Guid.NewGuid(), (decimal)amount)).IsValid);

    [Fact]
    public void OfficeCash_HasTwoDecimalsAtMost_AndAShortNote()
    {
        var validator = new RecordOfficeCashRequestValidator();

        Assert.False(validator.Validate(new RecordOfficeCashRequest(Guid.NewGuid(), 10.123m)).IsValid);
        Assert.False(validator.Validate(new RecordOfficeCashRequest(Guid.NewGuid(), 10m, new string('x', 101))).IsValid);
        Assert.False(validator.Validate(new RecordOfficeCashRequest(Guid.Empty, 10m)).IsValid);
    }

    [Fact]
    public void ACount_CanBeZero_ButNotNegative_AndHasTwoDecimalsAtMost()
    {
        var validator = new VerifySettlementRequestValidator();

        Assert.True(validator.Validate(new VerifySettlementRequest(0m)).IsValid);
        Assert.False(validator.Validate(new VerifySettlementRequest(-1m)).IsValid);
        Assert.False(validator.Validate(new VerifySettlementRequest(1.234m)).IsValid);
        Assert.False(validator.Validate(new VerifySettlementRequest(1m, new string('x', 501))).IsValid);
    }

    [Fact]
    public void SettlementsAndReports_CheckTheirFilters()
    {
        var settlements = new SettlementQueryValidator();
        Assert.True(settlements.Validate(new SettlementQuery { Status = "disputed" }).IsValid);
        Assert.False(settlements.Validate(new SettlementQuery { Status = "Draft" }).IsValid);
        Assert.False(settlements.Validate(new SettlementQuery { Status = "Maybe" }).IsValid);
        Assert.False(settlements.Validate(new SettlementQuery { From = new DateOnly(2026, 11, 12), To = new DateOnly(2026, 11, 10) }).IsValid);

        var reports = new PaymentReportQueryValidator();
        var day = new DateOnly(2026, 11, 1);
        Assert.True(reports.Validate(new PaymentReportQuery()).IsValid);
        Assert.True(reports.Validate(new PaymentReportQuery { From = day.AddDays(-365), To = day }).IsValid);
        Assert.False(reports.Validate(new PaymentReportQuery { From = day, To = day.AddDays(-1) }).IsValid);
        Assert.False(reports.Validate(new PaymentReportQuery { From = day.AddDays(-400), To = day }).IsValid);
    }

    [Fact]
    public void ResolvingNeedsAProperExplanation()
    {
        var validator = new ResolveSettlementRequestValidator();

        Assert.False(validator.Validate(new ResolveSettlementRequest("")).IsValid);
        Assert.False(validator.Validate(new ResolveSettlementRequest("ok")).IsValid);
        Assert.True(validator.Validate(new ResolveSettlementRequest("Deducted from pay")).IsValid);
    }

    [Fact]
    public void TransactionFilters_AreChecked()
    {
        var validator = new PaymentTransactionQueryValidator();

        Assert.True(validator.Validate(new PaymentTransactionQuery { Provider = "esewa", Status = "failed" }).IsValid);
        Assert.False(validator.Validate(new PaymentTransactionQuery { Provider = "Visa" }).IsValid);
        Assert.False(validator.Validate(new PaymentTransactionQuery { Status = "Maybe" }).IsValid);
        Assert.False(validator.Validate(new PaymentTransactionQuery { PageSize = 101 }).IsValid);
        Assert.False(validator.Validate(new PaymentTransactionQuery { Search = new string('x', 101) }).IsValid);
    }
}
