using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Contracts.Payments;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Payments;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;
using static NepalParcel.IntegrationTests.Infrastructure.DeliveryTestHelpers;

namespace NepalParcel.IntegrationTests;

/// <summary>
/// Paying for parcels in test mode: online payments through the simulated eSewa and Khalti, and cash at the office. The central
/// promise is that nothing is marked as paid until the server has asked the provider, and that what the provider says matches.
/// </summary>
public sealed class PaymentFlowTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private const string ReferencePattern = "^AFP[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{17}$";

    private static readonly Uri InitiateUri = new("/api/v1/payments/initiate", UriKind.Relative);
    private static readonly Uri OfficeCashUri = new("/api/v1/payments/office-cash", UriKind.Relative);
    private static readonly Uri TransactionsUri = new("/api/v1/payments/transactions", UriKind.Relative);

    private readonly BookingTestHelpers _booking = new(factory);
    private readonly DeliveryTestHelpers _delivery = new(factory);

    private static Uri CallbackUri(string provider) => new($"/api/v1/payments/callback/{provider.ToLowerInvariant()}", UriKind.Relative);

    private static Uri StatusUri(string reference) => new($"/api/v1/payments/status/{reference}", UriKind.Relative);

    private static Uri ParcelPaymentsUri(Guid parcelId) => new($"/api/v1/payments/parcel/{parcelId}", UriKind.Relative);

    private static Uri ReceiptUri(Guid paymentId) => new($"/api/v1/payments/{paymentId}/receipt", UriKind.Relative);

    private async Task<(HttpClient Customer, ParcelResponse Parcel, Guid Branch)> OnlineParcelAsync(string payment = "Esewa", Guid? branch = null)
    {
        var branchId = branch ?? await _booking.CreateBranchAsync();
        var customer = await _booking.CustomerAsync();
        return (customer, await BookAsync(customer, NewBooking(branchId, payment: payment)), branchId);
    }

    private static async Task<InitiatePaymentResponse> StartAsync(HttpClient client, Guid parcelId, string provider) =>
        await ReadAsync<InitiatePaymentResponse>(await client.PostAsJsonAsync(InitiateUri, new InitiatePaymentRequest(parcelId, provider)));

    private Task<HttpResponseMessage> CallbackAsync(string provider, string reference, Dictionary<string, string>? data = null) =>
        factory.CreateClient().PostAsJsonAsync(CallbackUri(provider), new PaymentCallbackRequest(reference, data));

    private Task<HttpResponseMessage> TesterAsync(string reference, string outcome) =>
        factory.CreateClient().PostAsJsonAsync(new Uri($"/api/v1/payments/test-gateway/{reference}/complete", UriKind.Relative), new TestGatewayCompleteRequest(outcome));

    private async Task<PaymentStatusResponse> VerifiedAsync(string provider, string reference) =>
        await ReadAsync<PaymentStatusResponse>(await CallbackAsync(provider, reference));

    private async Task<T> WithDbAsync<T>(Func<NepalParcelDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>());
    }

    private static async Task<PaymentResponse> SinglePaymentAsync(HttpClient client, Guid parcelId)
    {
        var payments = await ReadAsync<List<PaymentResponse>>(await client.GetAsync(ParcelPaymentsUri(parcelId)));
        return Assert.Single(payments);
    }

    // ---- paying online, the happy path -------------------------------------------------------------------------------

    [Theory]
    [InlineData("Esewa")]
    [InlineData("Khalti")]
    public async Task AnOnlinePayment_IsOnlyMarkedPaid_AfterTheServerHasAskedTheProvider(string provider)
    {
        var (customer, parcel, _) = await OnlineParcelAsync(provider);

        var init = await StartAsync(customer, parcel.Id, provider);

        Assert.Matches(ReferencePattern, init.Reference);
        Assert.Equal(provider, init.Provider);
        Assert.Equal(parcel.Price.TotalCharge, init.Amount);
        Assert.Equal("NPR", init.Currency);
        Assert.True(init.TestMode);
        Assert.Contains($"/pay/test/{provider.ToLowerInvariant()}/{init.Reference}", init.RedirectUrl, StringComparison.Ordinal);
        Assert.True(init.ExpiresAtUtc > DateTimeOffset.UtcNow);

        // Starting a payment is not paying. Nothing is paid yet, however the browser behaves.
        var started = await SinglePaymentAsync(customer, parcel.Id);
        Assert.Equal("Pending", started.Status);
        var attempt = Assert.Single(started.Transactions);
        Assert.Equal("Pending", attempt.Status);
        Assert.Null(attempt.ProviderReference);
        Assert.Equal(init.Reference, attempt.Reference);

        // The customer's browser comes back claiming success before the provider has been paid: believed by nobody.
        var forged = await ReadAsync<PaymentStatusResponse>(await CallbackAsync(
            provider,
            init.Reference,
            new Dictionary<string, string> { ["status"] = "COMPLETE", ["total_amount"] = "1", ["transaction_code"] = "FAKE-123" }));
        Assert.Equal("Pending", forged.Status);
        Assert.Equal("Pending", forged.PaymentStatus);
        Assert.Equal("Pending", (await SinglePaymentAsync(customer, parcel.Id)).Status);
        Assert.Equal("Pending", (await ReadAsync<ParcelResponse>(await customer.GetAsync(new Uri($"/api/v1/parcels/{parcel.Id}", UriKind.Relative)))).PaymentStatus);

        // The customer really pays at the (simulated) provider. Still nothing changes until the server verifies it.
        var session = await ReadAsync<TestGatewaySessionResponse>(await TesterAsync(init.Reference, "Success"));
        Assert.Equal("Paid", session.Status);
        Assert.StartsWith("TEST-", session.ProviderTransactionId, StringComparison.Ordinal);
        Assert.Equal("Pending", (await SinglePaymentAsync(customer, parcel.Id)).Status);

        var verified = await VerifiedAsync(provider, init.Reference);

        Assert.Equal("Succeeded", verified.Status);
        Assert.Equal("Paid", verified.PaymentStatus);
        Assert.Equal(parcel.TrackingNumber, verified.TrackingNumber);
        var paid = await SinglePaymentAsync(customer, parcel.Id);
        Assert.Equal("Paid", paid.Status);
        Assert.NotNull(paid.PaidAtUtc);
        Assert.StartsWith("PR-", paid.ReceiptNumber, StringComparison.Ordinal);
        var done = Assert.Single(paid.Transactions);
        Assert.Equal("Succeeded", done.Status);
        Assert.Equal(session.ProviderTransactionId, done.ProviderReference);
        Assert.Equal(parcel.Price.TotalCharge, done.Amount);
        Assert.Equal("Paid", (await ReadAsync<ParcelResponse>(await customer.GetAsync(new Uri($"/api/v1/parcels/{parcel.Id}", UriKind.Relative)))).PaymentStatus);

        // The same callback again (a refresh, a duplicate from the provider) changes nothing.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("Succeeded", (await VerifiedAsync(provider, init.Reference)).Status);
        }

        var count = await WithDbAsync(db => db.PaymentTransactions.CountAsync(t => t.PaymentId == paid.Id));
        var verifiedEntries = await WithDbAsync(db => db.AuditLogs.CountAsync(a => a.EntityId == paid.Id.ToString() && a.Action == "PaymentVerified"));
        Assert.Equal(1, count);
        Assert.Equal(1, verifiedEntries);

        var anonymousView = await ReadAsync<PaymentStatusResponse>(await factory.CreateClient().GetAsync(StatusUri(init.Reference)));
        Assert.Equal("Succeeded", anonymousView.Status);
        var receipt = await ReadAsync<PaymentReceiptResponse>(await customer.GetAsync(ReceiptUri(paid.Id)));
        Assert.Equal(paid.ReceiptNumber, receipt.ReceiptNumber);
        Assert.Equal(provider, receipt.Provider);
        Assert.Equal(session.ProviderTransactionId, receipt.ProviderReference);
        Assert.Equal(parcel.Price.TotalCharge, receipt.Amount);
        Assert.Equal(parcel.TrackingNumber, receipt.TrackingNumber);
        Assert.Equal("Afno Parcel Test Co", receipt.CompanyName);
    }

    [Fact]
    public async Task AFailedOrCancelledPayment_IsRecorded_LeavesTheChargeUnpaid_AndCanBeRetried()
    {
        var (customer, parcel, _) = await OnlineParcelAsync("Khalti");
        var first = await StartAsync(customer, parcel.Id, "Khalti");
        (await TesterAsync(first.Reference, "Failure")).EnsureSuccessStatusCode();

        var failed = await VerifiedAsync("Khalti", first.Reference);
        Assert.Equal("Failed", failed.Status);
        Assert.Equal("Pending", failed.PaymentStatus);
        Assert.False(string.IsNullOrWhiteSpace(failed.FailureReason));
        Assert.Equal("Failed", (await VerifiedAsync("Khalti", first.Reference)).Status); // final

        var second = await StartAsync(customer, parcel.Id, "Khalti");
        Assert.NotEqual(first.Reference, second.Reference);
        (await TesterAsync(second.Reference, "Cancel")).EnsureSuccessStatusCode();
        var cancelled = await VerifiedAsync("Khalti", second.Reference);
        Assert.Equal("Failed", cancelled.Status);
        Assert.Contains("cancel", cancelled.FailureReason, StringComparison.OrdinalIgnoreCase);

        var third = await StartAsync(customer, parcel.Id, "Khalti");
        (await TesterAsync(third.Reference, "Success")).EnsureSuccessStatusCode();
        Assert.Equal("Succeeded", (await VerifiedAsync("Khalti", third.Reference)).Status);

        var history = await SinglePaymentAsync(customer, parcel.Id);
        Assert.Equal("Paid", history.Status);
        Assert.Equal(["Failed", "Failed", "Succeeded"], history.Transactions.Select(t => t.Status));
        Assert.Equal([null, null], history.Transactions.Take(2).Select(t => t.ProviderReference));
        Assert.NotNull(history.Transactions[2].ProviderReference);
        Assert.Equal("Failed", (await ReadAsync<PaymentStatusResponse>(await factory.CreateClient().GetAsync(StatusUri(first.Reference)))).Status);
    }

    [Fact]
    public async Task StartingAgain_ReusesTheOpenAttempt_SoInitiationIsSafeToRepeat()
    {
        var (customer, parcel, _) = await OnlineParcelAsync();

        var first = await StartAsync(customer, parcel.Id, "Esewa");
        var again = await StartAsync(customer, parcel.Id, "Esewa");

        Assert.Equal(first.Reference, again.Reference);
        Assert.Equal(first.RedirectUrl, again.RedirectUrl);
        var payment = await SinglePaymentAsync(customer, parcel.Id);
        Assert.Single(payment.Transactions);
        var sessions = await WithDbAsync(db => db.TestGatewaySessions.CountAsync(s => s.MerchantReference == first.Reference));
        Assert.Equal(1, sessions);
    }

    [Fact]
    public async Task AnAttemptThatRunsOutOfTime_IsGivenUp_AndReplacedByANewOne()
    {
        var (customer, parcel, _) = await OnlineParcelAsync();
        var first = await StartAsync(customer, parcel.Id, "Esewa");
        await ExpireAsync(first.Reference);

        var verified = await VerifiedAsync("Esewa", first.Reference);
        var second = await StartAsync(customer, parcel.Id, "Esewa");

        Assert.Equal("Failed", verified.Status);
        Assert.Contains("in time", verified.FailureReason, StringComparison.Ordinal);
        Assert.NotEqual(first.Reference, second.Reference);

        // An attempt that is open and expired when a new one starts is closed by it.
        await ExpireAsync(second.Reference);
        var third = await StartAsync(customer, parcel.Id, "Esewa");
        var history = await SinglePaymentAsync(customer, parcel.Id);
        Assert.NotEqual(second.Reference, third.Reference);
        Assert.Equal(["Failed", "Failed", "Pending"], history.Transactions.Select(t => t.Status));
    }

    [Fact]
    public async Task APaymentThatWasCompletedAtTheProvider_IsAcceptedEvenIfItIsVerifiedLate()
    {
        var (customer, parcel, _) = await OnlineParcelAsync();
        var init = await StartAsync(customer, parcel.Id, "Esewa");
        (await TesterAsync(init.Reference, "Success")).EnsureSuccessStatusCode();
        await ExpireAsync(init.Reference); // the customer was slow to come back, but the money was taken

        var verified = await VerifiedAsync("Esewa", init.Reference);

        Assert.Equal("Succeeded", verified.Status);
        Assert.Equal("Paid", (await SinglePaymentAsync(customer, parcel.Id)).Status);
    }

    private Task ExpireAsync(string reference) =>
        WithDbAsync(async db =>
        {
            var tx = await db.PaymentTransactions.SingleAsync(t => t.MerchantReference == reference);
            db.Entry(tx).Property(t => t.ExpiresAtUtc).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            return 0;
        });

    // ---- what the provider says must match what we asked for ------------------------------------------------------------

    [Theory]
    [InlineData("amount")]
    [InlineData("currency")]
    public async Task ACompletedPayment_WithTheWrongAmountOrCurrency_IsRefused_AndNothingIsMarkedPaid(string tamper)
    {
        var (customer, parcel, _) = await OnlineParcelAsync();
        var init = await StartAsync(customer, parcel.Id, "Esewa");
        (await TesterAsync(init.Reference, "Success")).EnsureSuccessStatusCode();
        await WithDbAsync(async db =>
        {
            var session = await db.TestGatewaySessions.SingleAsync(s => s.MerchantReference == init.Reference);
            if (tamper == "amount")
            {
                db.Entry(session).Property(s => s.Amount).CurrentValue = 1.00m;
            }
            else
            {
                db.Entry(session).Property(s => s.Currency).CurrentValue = "USD";
            }

            await db.SaveChangesAsync();
            return 0;
        });

        var verified = await VerifiedAsync("Esewa", init.Reference);

        Assert.Equal("Failed", verified.Status);
        Assert.Equal("Pending", verified.PaymentStatus);
        Assert.Contains(tamper == "amount" ? "amount does not match" : "currency does not match", verified.FailureReason, StringComparison.Ordinal);
        var payment = await SinglePaymentAsync(customer, parcel.Id);
        Assert.Equal("Pending", payment.Status);
        Assert.Null(payment.Transactions[0].ProviderReference);

        var audit = await WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.EntityId == payment.Id.ToString()).Select(a => a.Action).ToListAsync());
        Assert.Contains("PaymentMismatch", audit);
        Assert.DoesNotContain("PaymentVerified", audit);

        // A fresh, honest attempt can still be made and paid.
        var retry = await StartAsync(customer, parcel.Id, "Esewa");
        (await TesterAsync(retry.Reference, "Success")).EnsureSuccessStatusCode();
        Assert.Equal("Succeeded", (await VerifiedAsync("Esewa", retry.Reference)).Status);
    }

    [Fact]
    public async Task AReferenceThatDoesNotExistOrBelongsToAnotherProvider_LooksLikeNothingAtAll()
    {
        var (customer, parcel, _) = await OnlineParcelAsync();
        var init = await StartAsync(customer, parcel.Id, "Esewa");

        Assert.Equal(HttpStatusCode.NotFound, (await CallbackAsync("Esewa", MerchantReferences.New())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CallbackAsync("Khalti", init.Reference)).StatusCode); // our reference, wrong provider
        Assert.Equal(HttpStatusCode.NotFound, (await CallbackAsync("Visa", init.Reference)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallbackAsync("Esewa", "not-a-reference")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallbackAsync("Esewa", string.Empty)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync(StatusUri(MerchantReferences.New()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync(StatusUri("short"))).StatusCode);
        var tooMuch = Enumerable.Range(0, 21).ToDictionary(i => $"k{i}", _ => "v");
        Assert.Equal(HttpStatusCode.BadRequest, (await CallbackAsync("Esewa", init.Reference, tooMuch)).StatusCode);
        Assert.Equal("Pending", (await SinglePaymentAsync(customer, parcel.Id)).Status);
    }

    [Fact]
    public async Task TheSimulatedProvider_CompletesAnAttemptOnlyOnce_AndOnlyForKnownReferences()
    {
        var (customer, parcel, _) = await OnlineParcelAsync();
        var init = await StartAsync(customer, parcel.Id, "Esewa");

        var page = await ReadAsync<TestGatewaySessionResponse>(await factory.CreateClient().GetAsync(new Uri($"/api/v1/payments/test-gateway/{init.Reference}", UriKind.Relative)));
        Assert.Equal("Pending", page.Status);
        Assert.Equal(parcel.Price.TotalCharge, page.Amount);
        Assert.Equal("Esewa", page.Provider);
        Assert.False(string.IsNullOrWhiteSpace(page.MerchantName));

        Assert.Equal(HttpStatusCode.OK, (await TesterAsync(init.Reference, "Success")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await TesterAsync(init.Reference, "Failure")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TesterAsync(MerchantReferences.New(), "Success")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TesterAsync(init.Reference, "Maybe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync(new Uri("/api/v1/payments/test-gateway/garbage", UriKind.Relative))).StatusCode);
    }

    // ---- who may do what ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyTheOwnerOrCounterStaff_CanStartAPayment()
    {
        var (customer, parcel, _) = await OnlineParcelAsync();
        var stranger = await _booking.CustomerAsync();
        var counter = await _booking.StaffAsync(Roles.BookingStaff);

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync(InitiateUri, new InitiatePaymentRequest(parcel.Id, "Esewa"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(InitiateUri, new InitiatePaymentRequest(parcel.Id, "Esewa"))).StatusCode);
        foreach (var role in new[] { Roles.Accountant, Roles.WarehouseStaff, Roles.DeliveryAgent })
        {
            var client = await _booking.StaffAsync(role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(InitiateUri, new InitiatePaymentRequest(parcel.Id, "Esewa"))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await counter.PostAsJsonAsync(InitiateUri, new InitiatePaymentRequest(parcel.Id, "Esewa"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.PostAsJsonAsync(InitiateUri, new InitiatePaymentRequest(parcel.Id, "Esewa"))).StatusCode);
    }

    [Fact]
    public async Task APaymentCanOnlyBeStarted_WithTheProviderTheParcelWasBookedWith_WhileItIsUnpaidAndNotCancelled()
    {
        var (customer, parcel, branch) = await OnlineParcelAsync("Esewa");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await customer.PostAsJsonAsync(InitiateUri, new InitiatePaymentRequest(parcel.Id, "Khalti"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync(InitiateUri, new InitiatePaymentRequest(parcel.Id, "Visa"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync(InitiateUri, new InitiatePaymentRequest(Guid.Empty, "Esewa"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.PostAsJsonAsync(InitiateUri, new InitiatePaymentRequest(Guid.NewGuid(), "Esewa"))).StatusCode);

        var cash = await BookAsync(customer, NewBooking(branch, payment: "Cash"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await customer.PostAsJsonAsync(InitiateUri, new InitiatePaymentRequest(cash.Id, "Esewa"))).StatusCode);

        var cancelled = await BookAsync(customer, NewBooking(branch, payment: "Esewa"));
        (await customer.PostAsJsonAsync(new Uri($"/api/v1/parcels/{cancelled.Id}/cancel", UriKind.Relative), new CancelParcelRequest("Changed my mind about this"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await customer.PostAsJsonAsync(InitiateUri, new InitiatePaymentRequest(cancelled.Id, "Esewa"))).StatusCode);

        var init = await StartAsync(customer, parcel.Id, "Esewa");
        (await TesterAsync(init.Reference, "Success")).EnsureSuccessStatusCode();
        Assert.Equal("Succeeded", (await VerifiedAsync("Esewa", init.Reference)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsJsonAsync(InitiateUri, new InitiatePaymentRequest(parcel.Id, "Esewa"))).StatusCode);
    }

    [Fact]
    public async Task PaymentHistoryAndReceipts_AreForTheOwnerAndStaffOnly()
    {
        var (customer, parcel, _) = await OnlineParcelAsync();
        var stranger = await _booking.CustomerAsync();
        var accountant = await _booking.StaffAsync(Roles.Accountant);
        var payment = await SinglePaymentAsync(accountant, parcel.Id);

        var unpaid = await customer.GetAsync(ReceiptUri(payment.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unpaid.StatusCode);
        Assert.Contains("only available once", await unpaid.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var init = await StartAsync(customer, parcel.Id, "Esewa");
        (await TesterAsync(init.Reference, "Success")).EnsureSuccessStatusCode();
        await VerifiedAsync("Esewa", init.Reference);

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(ParcelPaymentsUri(parcel.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(ReceiptUri(payment.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(ParcelPaymentsUri(parcel.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(ReceiptUri(payment.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync(ReceiptUri(payment.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await _booking.StaffAsync(Roles.BookingStaff)).GetAsync(ReceiptUri(payment.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await accountant.GetAsync(ReceiptUri(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await accountant.GetAsync(ParcelPaymentsUri(Guid.NewGuid()))).StatusCode);
    }

    // ---- cash at the office --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CashAtTheOffice_IsRecordedByCounterStaff_ForTheExactCharge_AndGetsAReceipt()
    {
        var (customer, parcel, _) = await OnlineParcelAsync("Cash");
        var counter = await _booking.StaffAsync(Roles.BookingStaff);
        var charge = parcel.Price.TotalCharge;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await counter.PostAsJsonAsync(OfficeCashUri, new RecordOfficeCashRequest(parcel.Id, charge - 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await counter.PostAsJsonAsync(OfficeCashUri, new RecordOfficeCashRequest(parcel.Id, charge + 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await counter.PostAsJsonAsync(OfficeCashUri, new RecordOfficeCashRequest(parcel.Id, 0m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await counter.PostAsJsonAsync(OfficeCashUri, new RecordOfficeCashRequest(parcel.Id, 10.123m))).StatusCode);
        Assert.Equal("Pending", (await SinglePaymentAsync(counter, parcel.Id)).Status);

        var recorded = await ReadAsync<PaymentResponse>(await counter.PostAsJsonAsync(OfficeCashUri, new RecordOfficeCashRequest(parcel.Id, charge, "Paid by the sender in person")));

        Assert.Equal("Paid", recorded.Status);
        Assert.Equal("Paid by the sender in person", recorded.Reference);
        var entry = Assert.Single(recorded.Transactions);
        Assert.Equal("Cash", entry.Provider);
        Assert.Equal("Succeeded", entry.Status);
        Assert.Null(entry.ProviderReference);
        Assert.Equal(charge, entry.Amount);
        var receipt = await ReadAsync<PaymentReceiptResponse>(await customer.GetAsync(ReceiptUri(recorded.Id)));
        Assert.Equal("Branch counter", receipt.ReceivedBy);
        Assert.Equal("Cash", receipt.Provider);
        var recordedBy = await WithDbAsync(db => db.PaymentTransactions.AsNoTracking().Where(t => t.PaymentId == recorded.Id).Select(t => t.RecordedByUserId).SingleAsync());
        Assert.NotNull(recordedBy);

        Assert.Equal(HttpStatusCode.Conflict, (await counter.PostAsJsonAsync(OfficeCashUri, new RecordOfficeCashRequest(parcel.Id, charge))).StatusCode);
    }

    [Fact]
    public async Task OfficeCash_IsOnlyForCashParcels_AndOnlyForCounterStaff()
    {
        var (customer, online, branch) = await OnlineParcelAsync("Esewa");
        var cash = await BookAsync(customer, NewBooking(branch, payment: "Cash"));
        var counter = await _booking.StaffAsync(Roles.BookingStaff);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await counter.PostAsJsonAsync(OfficeCashUri, new RecordOfficeCashRequest(online.Id, online.Price.TotalCharge))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await counter.PostAsJsonAsync(OfficeCashUri, new RecordOfficeCashRequest(Guid.NewGuid(), 100m))).StatusCode);
        foreach (var role in new[] { Roles.Customer, Roles.Accountant, Roles.WarehouseStaff, Roles.DeliveryAgent })
        {
            var client = await _booking.StaffAsync(role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(OfficeCashUri, new RecordOfficeCashRequest(cash.Id, cash.Price.TotalCharge))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(OfficeCashUri, new RecordOfficeCashRequest(cash.Id, cash.Price.TotalCharge))).StatusCode);
        Assert.Equal("Pending", (await SinglePaymentAsync(counter, cash.Id)).Status);
    }

    [Fact]
    public async Task CashAndWalletPaymentsTakenAtTheCounter_AreInTheLedgerToo()
    {
        var branch = await _booking.CreateBranchAsync();
        var counter = await _booking.StaffAsync(Roles.BookingStaff);
        var atCounter = await BookAsync(counter, NewBooking(branch, payment: "Cash"));
        var wallet = await BookAsync(counter, NewBooking(branch, payment: "Esewa"));

        var cash = await SinglePaymentAsync(counter, atCounter.Id);
        var transfer = await SinglePaymentAsync(counter, wallet.Id);

        Assert.Equal("Paid", cash.Status);
        Assert.Equal("Cash", Assert.Single(cash.Transactions).Provider);
        Assert.Equal("Paid", transfer.Status);
        var tx = Assert.Single(transfer.Transactions);
        Assert.Equal("Esewa", tx.Provider);
        Assert.Equal("Succeeded", tx.Status);
        Assert.Equal(HttpStatusCode.OK, (await counter.GetAsync(ReceiptUri(cash.Id))).StatusCode);
    }

    [Fact]
    public async Task APaymentConfirmedByHand_IsInTheLedger()
    {
        var (customer, parcel, _) = await OnlineParcelAsync("BankTransfer");
        var counter = await _booking.StaffAsync(Roles.BookingStaff);

        (await counter.PostAsJsonAsync(new Uri($"/api/v1/parcels/{parcel.Id}/confirm-payment", UriKind.Relative), new ConfirmPaymentRequest("BANK-REF-77"))).EnsureSuccessStatusCode();

        var payment = await SinglePaymentAsync(customer, parcel.Id);
        Assert.Equal("Paid", payment.Status);
        Assert.Equal("BankTransfer", Assert.Single(payment.Transactions).Provider);
    }

    // ---- the accountant's transaction list -------------------------------------------------------------------------------

    [Fact]
    public async Task TheTransactionList_ShowsEveryAttempt_AndCanBeFiltered()
    {
        var branch = await _booking.CreateBranchAsync();
        var accountant = await _booking.StaffAsync(Roles.Accountant);
        var (customer, esewa, _) = await OnlineParcelAsync("Esewa", branch);
        var (_, khalti, _) = await OnlineParcelAsync("Khalti", branch);
        var counter = await _booking.StaffAsync(Roles.BookingStaff);
        var cash = await BookAsync(counter, NewBooking(branch, payment: "Cash"));
        var paidAttempt = await StartAsync(customer, esewa.Id, "Esewa");
        (await TesterAsync(paidAttempt.Reference, "Success")).EnsureSuccessStatusCode();
        await VerifiedAsync("Esewa", paidAttempt.Reference);
        var khaltiCustomer = await _booking.CustomerAsync();
        var khaltiParcel = await BookAsync(khaltiCustomer, NewBooking(branch, payment: "Khalti"));
        var failedAttempt = await StartAsync(khaltiCustomer, khaltiParcel.Id, "Khalti");
        (await TesterAsync(failedAttempt.Reference, "Failure")).EnsureSuccessStatusCode();
        await VerifiedAsync("Khalti", failedAttempt.Reference);
        Assert.NotEqual(Guid.Empty, khalti.Id);

        async Task<PagedResponse<PaymentTransactionRow>> ListAsync(string query) =>
            await ReadAsync<PagedResponse<PaymentTransactionRow>>(await accountant.GetAsync(new Uri($"{TransactionsUri}?branchId={branch}&{query}", UriKind.Relative)));

        var all = await ListAsync(string.Empty);
        Assert.Equal(3, all.TotalCount); // the esewa success, the khalti failure and the counter cash
        Assert.Equal(1, (await ListAsync("provider=Esewa")).TotalCount);
        Assert.Equal(1, (await ListAsync("provider=Cash")).TotalCount);
        var failed = Assert.Single((await ListAsync("status=Failed")).Items);
        Assert.Equal(failedAttempt.Reference, failed.Reference);
        Assert.Equal(khaltiParcel.TrackingNumber, failed.TrackingNumber);
        Assert.Equal(1, (await ListAsync($"search={paidAttempt.Reference[3..10]}")).TotalCount);
        Assert.Equal(1, (await ListAsync($"search={cash.TrackingNumber}")).TotalCount);
        Assert.Equal(1, (await ListAsync($"parcelId={esewa.Id}")).TotalCount);
        var done = Assert.Single((await ListAsync("status=Succeeded&provider=Esewa")).Items);
        Assert.StartsWith("TEST-", done.ProviderReference, StringComparison.Ordinal);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.75)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.75).AddDays(1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(3, (await ListAsync($"from={today}&to={today}")).TotalCount);
        Assert.Equal(0, (await ListAsync($"from={tomorrow}")).TotalCount);
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.GetAsync(new Uri($"{TransactionsUri}?provider=Visa", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.GetAsync(new Uri($"{TransactionsUri}?status=Maybe", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.GetAsync(new Uri($"{TransactionsUri}?from={tomorrow}&to={today}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task TheTransactionList_IsForFinanceStaff_AndABranchManagerSeesOnlyTheirOwnBranch()
    {
        var own = await _booking.CreateBranchAsync();
        var other = await _booking.CreateBranchAsync();
        var manager = await _booking.StaffAtAsync(Roles.BranchManager, own);
        var counter = await _booking.StaffAsync(Roles.BookingStaff);
        var mine = await BookAsync(counter, NewBooking(own, payment: "Cash"));
        var theirs = await BookAsync(counter, NewBooking(other, payment: "Cash"));

        var list = await ReadAsync<PagedResponse<PaymentTransactionRow>>(await manager.GetAsync(new Uri($"{TransactionsUri}?pageSize=100", UriKind.Relative)));

        Assert.Contains(list.Items, t => t.ParcelId == mine.Id);
        Assert.DoesNotContain(list.Items, t => t.ParcelId == theirs.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(new Uri($"{TransactionsUri}?branchId={other}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _booking.StaffAsync(Roles.BranchManager)).GetAsync(TransactionsUri)).StatusCode);
        foreach (var role in new[] { Roles.WarehouseStaff, Roles.BookingStaff, Roles.DeliveryAgent, Roles.Customer })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await (await _booking.StaffAsync(role)).GetAsync(TransactionsUri)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(TransactionsUri)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await _booking.StaffAsync(Roles.Accountant)).GetAsync(new Uri($"{TransactionsUri}?branchId={other}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task MerchantReferences_AreUniqueInTheDatabase()
    {
        var (customer, parcel, _) = await OnlineParcelAsync();
        var init = await StartAsync(customer, parcel.Id, "Esewa");

        var rejected = await WithDbAsync(async db =>
        {
            var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.ParcelId == parcel.Id);
            db.PaymentTransactions.Add(PaymentTransaction.Create(payment.Id, TransactionType.Charge, "Esewa", payment.Amount, DateTimeOffset.UtcNow, merchantReference: init.Reference));
            return await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });

        Assert.NotNull(rejected);
    }
}

/// <summary>Live mode refuses online payments (no real gateway is built in) and the simulated provider does not exist.</summary>
public sealed class PaymentLiveModeTests : IAsyncLifetime
{
    private readonly NepalParcelApiFactory _factory = new() { Settings = { ["Payments:Mode"] = "Live" } };

    public async Task InitializeAsync() => await ((IAsyncLifetime)_factory).InitializeAsync();

    public async Task DisposeAsync() => await ((IAsyncLifetime)_factory).DisposeAsync();

    [Fact]
    public async Task InLiveMode_OnlinePaymentsAreRefused_AndTheTestProviderIsGone()
    {
        var booking = new BookingTestHelpers(_factory);
        var branch = await booking.CreateBranchAsync();
        var customer = await booking.CustomerAsync();
        var parcel = await BookAsync(customer, NewBooking(branch, payment: "Esewa"));

        var initiate = await customer.PostAsJsonAsync(new Uri("/api/v1/payments/initiate", UriKind.Relative), new InitiatePaymentRequest(parcel.Id, "Esewa"));
        var page = await _factory.CreateClient().GetAsync(new Uri($"/api/v1/payments/test-gateway/{MerchantReferences.New()}", UriKind.Relative));
        var complete = await _factory.CreateClient().PostAsJsonAsync(
            new Uri($"/api/v1/payments/test-gateway/{MerchantReferences.New()}/complete", UriKind.Relative),
            new TestGatewayCompleteRequest("Success"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, initiate.StatusCode);
        Assert.Contains("not available", await initiate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);
        var payments = await ReadAsync<List<PaymentResponse>>(await customer.GetAsync(new Uri($"/api/v1/payments/parcel/{parcel.Id}", UriKind.Relative)));
        Assert.All(payments, p => Assert.Empty(p.Transactions));
        Assert.Equal("Pending", Assert.Single(payments).Status);
    }
}
