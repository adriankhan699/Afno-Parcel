using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Delivery;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Contracts.Payments;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Payments;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;
using static NepalParcel.IntegrationTests.Infrastructure.DeliveryTestHelpers;

namespace NepalParcel.IntegrationTests;

/// <summary>Cash on delivery from the doorstep to the accountant: collection, hand-in, counting, disputes and reports.</summary>
public sealed class CodSettlementTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private static readonly Uri HandInUri = new("/api/v1/agent/settlements", UriKind.Relative);
    private static readonly Uri MyCashUri = new("/api/v1/agent/cash", UriKind.Relative);
    private static readonly Uri SettlementsUri = new("/api/v1/payments/settlements", UriKind.Relative);
    private static readonly Uri AgentCashUri = new("/api/v1/payments/agent-cash", UriKind.Relative);
    private static readonly Uri ReportUri = new("/api/v1/payments/reports/summary", UriKind.Relative);

    private readonly BookingTestHelpers _booking = new(factory);
    private readonly DeliveryTestHelpers _delivery = new(factory);

    private sealed record Scene(Guid Branch, HttpClient Admin, HttpClient Accountant, HttpClient Agent, DeliveryAgentResponse AgentInfo);

    private async Task<Scene> NewSceneAsync()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (agent, info, _, _) = await _delivery.NewAgentAsync(branch);
        return new Scene(branch, admin, await _booking.StaffAsync(Roles.Accountant), agent, info);
    }

    /// <summary>The agent takes a parcel out and collects its cash (the goods money, paid in cash).</summary>
    private async Task<(ParcelResponse Parcel, DeliveryAssignmentResponse Job)> CollectAsync(Scene s, decimal goods)
    {
        var parcel = await _delivery.ReadyParcelAsync(s.Branch, NewBooking(s.Branch, payment: "Cash", cod: goods));
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, parcel.Id, s.AgentInfo.Id);
        (await s.Agent.PostAsJsonAsync(AgentAssignmentUri(job.Id, "/collect-cod"), new CollectCodRequest(parcel.CodAmount))).EnsureSuccessStatusCode();
        return (parcel, job);
    }

    private static Uri SettlementUri(Guid id, string suffix = "") => new($"/api/v1/payments/settlements/{id}{suffix}", UriKind.Relative);

    private static async Task<AgentCashPositionRow> MyPositionAsync(HttpClient agent) =>
        (await ReadAsync<AgentCashSummaryResponse>(await agent.GetAsync(MyCashUri))).Position;

    private static async Task<SettlementResponse> HandInAsync(HttpClient agent, string? remarks = null) =>
        await ReadAsync<SettlementResponse>(await agent.PostAsJsonAsync(HandInUri, new CreateSettlementRequest(remarks)));

    // ---- cash on delivery in the ledger -------------------------------------------------------------------------------------

    [Fact]
    public async Task ACashOnDeliveryDelivery_PutsTheDeliveryChargeInTheLedger_AndGetsAReceipt()
    {
        var s = await NewSceneAsync();
        var parcel = await _delivery.ReadyParcelAsync(s.Branch, NewBooking(s.Branch, payment: "CashOnDelivery", cod: 1000m));
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, parcel.Id, s.AgentInfo.Id);
        await _delivery.ProveByCodeAsync(s.Agent, job.Id, parcel.Id);
        (await s.Agent.PostAsJsonAsync(AgentAssignmentUri(job.Id, "/collect-cod"), new CollectCodRequest(parcel.CodAmount))).EnsureSuccessStatusCode();
        (await s.Agent.PostAsJsonAsync(AgentAssignmentUri(job.Id, "/complete"), new CompleteDeliveryRequest())).EnsureSuccessStatusCode();

        var payments = await ReadAsync<List<PaymentResponse>>(await s.Accountant.GetAsync(new Uri($"/api/v1/payments/parcel/{parcel.Id}", UriKind.Relative)));

        var payment = Assert.Single(payments);
        Assert.Equal("Paid", payment.Status);
        Assert.Equal(parcel.Price.TotalCharge, payment.Amount); // the delivery charge; the goods money is not a payment
        var entry = Assert.Single(payment.Transactions);
        Assert.Equal("CashOnDelivery", entry.Provider);
        Assert.Equal("Succeeded", entry.Status);
        var receipt = await ReadAsync<PaymentReceiptResponse>(await s.Accountant.GetAsync(new Uri($"/api/v1/payments/{payment.Id}/receipt", UriKind.Relative)));
        Assert.Equal("Delivery agent", receipt.ReceivedBy);
        Assert.Equal(parcel.CodAmount, parcel.Price.TotalCharge + 1000m);
    }

    // ---- the agent hands in cash ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAgentsCashPosition_FollowsTheCashFromTheDoorToTheAccountant()
    {
        var s = await NewSceneAsync();
        await CollectAsync(s, 800m);
        await CollectAsync(s, 700m);

        var before = await MyPositionAsync(s.Agent);
        Assert.Equal(1500m, before.Collected);
        Assert.Equal(1500m, before.InHand);
        Assert.Equal(0m, before.InSettlement);
        Assert.Equal(2, before.CollectionCount);
        Assert.NotNull(before.LastCollectionAtUtc);

        var handIn = await HandInAsync(s.Agent, "Handed in at the branch counter");

        Assert.Matches("^SET-\\d{8}-[A-Z2-9]{6}$", handIn.ReferenceNumber);
        Assert.Equal("Submitted", handIn.Status);
        Assert.Equal(1500m, handIn.TotalAmount);
        Assert.Equal(2, handIn.CollectionCount);
        Assert.Null(handIn.CountedAmount);
        Assert.Equal(s.AgentInfo.Id, handIn.AgentId);
        Assert.Equal("Test Branch", handIn.BranchName);
        Assert.Equal("Handed in at the branch counter", handIn.Remarks);
        Assert.Equal(2, handIn.Collections.Count);
        Assert.All(handIn.Collections, c => Assert.Equal("InSettlement", c.Status));

        var waiting = await MyPositionAsync(s.Agent);
        Assert.Equal(0m, waiting.InHand);
        Assert.Equal(1500m, waiting.InSettlement);
        Assert.Equal(0m, (await ReadAsync<AgentSummaryResponse>(await s.Agent.GetAsync(AgentMeUri))).CodCollectedUnsettled);

        var none = await s.Agent.PostAsJsonAsync(HandInUri, new CreateSettlementRequest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        Assert.Contains("no cash to hand in", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var accepted = await ReadAsync<SettlementResponse>(await s.Accountant.PostAsJsonAsync(SettlementUri(handIn.Id, "/verify"), new VerifySettlementRequest(1500m)));

        Assert.Equal("Verified", accepted.Status);
        Assert.Equal(1500m, accepted.CountedAmount);
        Assert.Equal(0m, accepted.Variance);
        Assert.NotNull(accepted.VerifiedAtUtc);
        Assert.All(accepted.Collections, c => Assert.Equal("Settled", c.Status));
        var settled = await MyPositionAsync(s.Agent);
        Assert.Equal(1500m, settled.Settled);
        Assert.Equal(0m, settled.InSettlement);
        Assert.Equal(0m, settled.InHand);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Accountant.PostAsJsonAsync(SettlementUri(handIn.Id, "/verify"), new VerifySettlementRequest(1500m))).StatusCode);
    }

    [Fact]
    public async Task ACountThatDiffers_NeedsAnExplanation_AndLeavesTheSettlementDisputed_UntilItIsResolved()
    {
        var s = await NewSceneAsync();
        await CollectAsync(s, 800m);
        await CollectAsync(s, 700m);
        var handIn = await HandInAsync(s.Agent);

        var unexplained = await s.Accountant.PostAsJsonAsync(SettlementUri(handIn.Id, "/verify"), new VerifySettlementRequest(1400m));
        Assert.Equal(HttpStatusCode.BadRequest, unexplained.StatusCode);
        Assert.Contains("Explain the difference", await unexplained.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("Submitted", (await ReadAsync<SettlementResponse>(await s.Accountant.GetAsync(SettlementUri(handIn.Id)))).Status);

        var disputed = await ReadAsync<SettlementResponse>(
            await s.Accountant.PostAsJsonAsync(SettlementUri(handIn.Id, "/verify"), new VerifySettlementRequest(1400m, "Short by NPR 100; the agent says a customer paid with a torn note")));

        Assert.Equal("Disputed", disputed.Status);
        Assert.Equal(1500m, disputed.TotalAmount);
        Assert.Equal(1400m, disputed.CountedAmount);
        Assert.Equal(-100m, disputed.Variance);
        Assert.Contains("torn note", disputed.Remarks, StringComparison.Ordinal);
        Assert.All(disputed.Collections, c => Assert.Equal("Disputed", c.Status));
        var position = await MyPositionAsync(s.Agent);
        Assert.Equal(1500m, position.Disputed);
        Assert.Equal(0m, position.Settled);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Accountant.PostAsJsonAsync(SettlementUri(handIn.Id, "/verify"), new VerifySettlementRequest(1500m))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Accountant.PostAsJsonAsync(SettlementUri(handIn.Id, "/resolve"), new ResolveSettlementRequest("ok"))).StatusCode);
        var resolved = await ReadAsync<SettlementResponse>(
            await s.Accountant.PostAsJsonAsync(SettlementUri(handIn.Id, "/resolve"), new ResolveSettlementRequest("Investigated: NPR 100 deducted from the agent's pay")));

        Assert.Equal("Verified", resolved.Status);
        Assert.Equal(-100m, resolved.Variance); // the shortage stays on the record
        Assert.Contains("deducted", resolved.Remarks, StringComparison.Ordinal);
        Assert.All(resolved.Collections, c => Assert.Equal("Settled", c.Status));
        Assert.Equal(1500m, (await MyPositionAsync(s.Agent)).Settled);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Accountant.PostAsJsonAsync(SettlementUri(handIn.Id, "/resolve"), new ResolveSettlementRequest("Resolving it twice"))).StatusCode);
    }

    [Fact]
    public async Task AnExcessIsADisputeToo()
    {
        var s = await NewSceneAsync();
        await CollectAsync(s, 500m);
        var handIn = await HandInAsync(s.Agent);

        var disputed = await ReadAsync<SettlementResponse>(
            await s.Accountant.PostAsJsonAsync(SettlementUri(handIn.Id, "/verify"), new VerifySettlementRequest(handIn.TotalAmount + 50m, "NPR 50 more than recorded")));

        Assert.Equal("Disputed", disputed.Status);
        Assert.Equal(50m, disputed.Variance);
    }

    [Fact]
    public async Task OnlyCashNotYetHandedIn_GoesIntoTheNextHandIn()
    {
        var s = await NewSceneAsync();
        await CollectAsync(s, 400m);
        var first = await HandInAsync(s.Agent);
        var (late, _) = await CollectAsync(s, 600m);

        var second = await HandInAsync(s.Agent);

        Assert.Equal(400m, first.TotalAmount);
        Assert.Equal(600m, second.TotalAmount);
        Assert.Equal(late.TrackingNumber, Assert.Single(second.Collections).TrackingNumber);
        Assert.NotEqual(first.ReferenceNumber, second.ReferenceNumber);
        var mine = await ReadAsync<PagedResponse<SettlementResponse>>(await s.Agent.GetAsync(new Uri($"{HandInUri}?pageSize=10", UriKind.Relative)));
        Assert.Equal(2, mine.TotalCount);
        Assert.True(mine.Items[0].CreatedAtUtc >= mine.Items[1].CreatedAtUtc);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Agent.GetAsync(new Uri($"{HandInUri}?pageSize=1000", UriKind.Relative))).StatusCode);
        var summary = await ReadAsync<AgentCashSummaryResponse>(await s.Agent.GetAsync(MyCashUri));
        Assert.Equal(2, summary.RecentSettlements.Count);
    }

    // ---- the accountant's view ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAccountant_SeesEveryAgentsCash_AndTheSettlementList()
    {
        var s = await NewSceneAsync();
        var (otherAgent, otherInfo, _, _) = await _delivery.NewAgentAsync(s.Branch);
        await CollectAsync(s, 800m);
        var parcel = await _delivery.ReadyParcelAsync(s.Branch, NewBooking(s.Branch, payment: "Cash", cod: 250m));
        var job = await _delivery.StartedAsync(s.Admin, otherAgent, parcel.Id, otherInfo.Id);
        (await otherAgent.PostAsJsonAsync(AgentAssignmentUri(job.Id, "/collect-cod"), new CollectCodRequest(parcel.CodAmount))).EnsureSuccessStatusCode();
        var handIn = await HandInAsync(s.Agent);

        var rows = await ReadAsync<List<AgentCashPositionRow>>(await s.Accountant.GetAsync(new Uri($"{AgentCashUri}?branchId={s.Branch}", UriKind.Relative)));
        var list = await ReadAsync<PagedResponse<SettlementResponse>>(await s.Accountant.GetAsync(new Uri($"{SettlementsUri}?branchId={s.Branch}", UriKind.Relative)));
        var detail = await ReadAsync<SettlementResponse>(await s.Accountant.GetAsync(SettlementUri(handIn.Id)));

        var mine = Assert.Single(rows, r => r.AgentId == s.AgentInfo.Id);
        Assert.Equal(800m, mine.InSettlement);
        Assert.Equal(0m, mine.InHand);
        var theirs = Assert.Single(rows, r => r.AgentId == otherInfo.Id);
        Assert.Equal(250m, theirs.InHand);
        Assert.Equal("Test Branch", mine.BranchName);
        Assert.Equal(handIn.Id, Assert.Single(list.Items).Id);
        Assert.Empty(list.Items[0].Collections); // collections only in the detail view
        Assert.Single(detail.Collections);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Accountant.GetAsync(SettlementUri(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Accountant.PostAsJsonAsync(SettlementUri(Guid.NewGuid(), "/verify"), new VerifySettlementRequest(1m))).StatusCode);
    }

    [Fact]
    public async Task TheSettlementList_CanBeFilteredByStatusAgentAndDate()
    {
        var s = await NewSceneAsync();
        await CollectAsync(s, 300m);
        var one = await HandInAsync(s.Agent);
        await CollectAsync(s, 200m);
        var two = await HandInAsync(s.Agent);
        (await s.Accountant.PostAsJsonAsync(SettlementUri(one.Id, "/verify"), new VerifySettlementRequest(300m))).EnsureSuccessStatusCode();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.75)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.75).AddDays(1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        async Task<PagedResponse<SettlementResponse>> ListAsync(string query) =>
            await ReadAsync<PagedResponse<SettlementResponse>>(await s.Accountant.GetAsync(new Uri($"{SettlementsUri}?branchId={s.Branch}&{query}", UriKind.Relative)));

        Assert.Equal(2, (await ListAsync(string.Empty)).TotalCount);
        Assert.Equal(one.Id, Assert.Single((await ListAsync("status=Verified")).Items).Id);
        Assert.Equal(two.Id, Assert.Single((await ListAsync("status=Submitted")).Items).Id);
        Assert.Equal(0, (await ListAsync("status=Disputed")).TotalCount);
        Assert.Equal(2, (await ListAsync($"agentId={s.AgentInfo.Id}")).TotalCount);
        Assert.Equal(0, (await ListAsync($"agentId={Guid.NewGuid()}")).TotalCount);
        Assert.Equal(2, (await ListAsync($"from={today}&to={today}")).TotalCount);
        Assert.Equal(0, (await ListAsync($"from={tomorrow}")).TotalCount);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Accountant.GetAsync(new Uri($"{SettlementsUri}?status=Draft", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Accountant.GetAsync(new Uri($"{SettlementsUri}?status=Maybe", UriKind.Relative))).StatusCode);
    }

    // ---- who may do what -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyFinanceStaffCanCountCash_AndABranchManagerOnlyForTheirOwnBranch()
    {
        var s = await NewSceneAsync();
        var otherBranch = await _booking.CreateBranchAsync();
        var manager = await _booking.StaffAtAsync(Roles.BranchManager, s.Branch);
        var foreignManager = await _booking.StaffAtAsync(Roles.BranchManager, otherBranch);
        await CollectAsync(s, 500m);
        var handIn = await HandInAsync(s.Agent);

        foreach (var role in new[] { Roles.WarehouseStaff, Roles.BookingStaff, Roles.DeliveryAgent, Roles.Customer })
        {
            var client = await _booking.StaffAsync(role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(SettlementsUri)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(SettlementUri(handIn.Id))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(SettlementUri(handIn.Id, "/verify"), new VerifySettlementRequest(500m))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(AgentCashUri)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(ReportUri)).StatusCode);
        }

        // The agent who handed the cash in cannot count it either.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Agent.PostAsJsonAsync(SettlementUri(handIn.Id, "/verify"), new VerifySettlementRequest(500m))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(SettlementsUri)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await foreignManager.GetAsync(SettlementUri(handIn.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreignManager.PostAsJsonAsync(SettlementUri(handIn.Id, "/verify"), new VerifySettlementRequest(500m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreignManager.GetAsync(new Uri($"{AgentCashUri}?branchId={s.Branch}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _booking.StaffAsync(Roles.BranchManager)).GetAsync(SettlementsUri)).StatusCode);

        var seen = await ReadAsync<PagedResponse<SettlementResponse>>(await manager.GetAsync(SettlementsUri));
        Assert.Contains(seen.Items, x => x.Id == handIn.Id);
        var theirList = await ReadAsync<PagedResponse<SettlementResponse>>(await foreignManager.GetAsync(SettlementsUri));
        Assert.DoesNotContain(theirList.Items, x => x.Id == handIn.Id);
        Assert.Equal("Verified", (await ReadAsync<SettlementResponse>(await manager.PostAsJsonAsync(SettlementUri(handIn.Id, "/verify"), new VerifySettlementRequest(500m)))).Status);
    }

    [Fact]
    public async Task TheAgentCashApi_IsForAgentsOnly_AndEachAgentSeesOnlyTheirOwn()
    {
        var s = await NewSceneAsync();
        var (other, _, _, _) = await _delivery.NewAgentAsync(s.Branch);
        await CollectAsync(s, 500m);
        await HandInAsync(s.Agent);

        var theirs = await ReadAsync<PagedResponse<SettlementResponse>>(await other.GetAsync(HandInUri));
        var theirCash = await MyPositionAsync(other);

        Assert.Empty(theirs.Items);
        Assert.Equal(0m, theirCash.Collected);
        foreach (var role in new[] { Roles.SuperAdmin, Roles.Accountant, Roles.BranchManager, Roles.Customer, Roles.WarehouseStaff })
        {
            var client = await _booking.StaffAsync(role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(MyCashUri)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(HandInUri, new CreateSettlementRequest())).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(MyCashUri)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await other.PostAsJsonAsync(HandInUri, new CreateSettlementRequest())).StatusCode); // nothing of their own to hand in
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Agent.PostAsJsonAsync(HandInUri, new CreateSettlementRequest(new string('x', 301)))).StatusCode);
    }

    [Fact]
    public async Task EverySettlementStep_IsInTheAuditTrail()
    {
        var s = await NewSceneAsync();
        await CollectAsync(s, 500m);
        var handIn = await HandInAsync(s.Agent);
        (await s.Accountant.PostAsJsonAsync(SettlementUri(handIn.Id, "/verify"), new VerifySettlementRequest(450m, "Short by fifty"))).EnsureSuccessStatusCode();
        (await s.Accountant.PostAsJsonAsync(SettlementUri(handIn.Id, "/resolve"), new ResolveSettlementRequest("Deducted from pay"))).EnsureSuccessStatusCode();

        var entries = await WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.EntityId == handIn.Id.ToString()).ToListAsync());

        Assert.Contains(entries, e => e.Action == "SettlementSubmitted");
        var disputed = Assert.Single(entries, e => e.Action == "SettlementDisputed");
        Assert.Contains("450", disputed.NewValues, StringComparison.Ordinal);
        Assert.Contains(entries, e => e.Action == "SettlementResolved");
        Assert.All(entries, e => Assert.NotNull(e.UserId));
    }

    private async Task<T> WithDbAsync<T>(Func<NepalParcelDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>());
    }

    // ---- the accountant's report -------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSummaryReport_AddsUpPaymentsByMethodAndProvider_AndTheCashPosition()
    {
        var s = await NewSceneAsync();
        var customer = await _booking.CustomerAsync();
        var counter = await _booking.StaffAsync(Roles.BookingStaff);

        var esewa = await BookAsync(customer, NewBooking(s.Branch, payment: "Esewa"));
        var init = await ReadAsync<InitiatePaymentResponse>(await customer.PostAsJsonAsync(new Uri("/api/v1/payments/initiate", UriKind.Relative), new InitiatePaymentRequest(esewa.Id, "Esewa")));
        (await factory.CreateClient().PostAsJsonAsync(new Uri($"/api/v1/payments/test-gateway/{init.Reference}/complete", UriKind.Relative), new TestGatewayCompleteRequest("Success"))).EnsureSuccessStatusCode();
        (await factory.CreateClient().PostAsJsonAsync(new Uri("/api/v1/payments/callback/esewa", UriKind.Relative), new PaymentCallbackRequest(init.Reference))).EnsureSuccessStatusCode();

        var cashParcel = await BookAsync(customer, NewBooking(s.Branch, payment: "Cash"));
        (await counter.PostAsJsonAsync(new Uri("/api/v1/payments/office-cash", UriKind.Relative), new RecordOfficeCashRequest(cashParcel.Id, cashParcel.Price.TotalCharge))).EnsureSuccessStatusCode();

        var khalti = await BookAsync(customer, NewBooking(s.Branch, payment: "Khalti")); // booked, never paid
        var failedParcel = await BookAsync(customer, NewBooking(s.Branch, payment: "Khalti"));
        var failedInit = await ReadAsync<InitiatePaymentResponse>(await customer.PostAsJsonAsync(new Uri("/api/v1/payments/initiate", UriKind.Relative), new InitiatePaymentRequest(failedParcel.Id, "Khalti")));
        (await factory.CreateClient().PostAsJsonAsync(new Uri($"/api/v1/payments/test-gateway/{failedInit.Reference}/complete", UriKind.Relative), new TestGatewayCompleteRequest("Failure"))).EnsureSuccessStatusCode();
        (await factory.CreateClient().PostAsJsonAsync(new Uri("/api/v1/payments/callback/khalti", UriKind.Relative), new PaymentCallbackRequest(failedInit.Reference))).EnsureSuccessStatusCode();

        var (settled, _) = await CollectAsync(s, 800m);
        var firstHandIn = await HandInAsync(s.Agent);
        (await s.Accountant.PostAsJsonAsync(SettlementUri(firstHandIn.Id, "/verify"), new VerifySettlementRequest(800m))).EnsureSuccessStatusCode();
        await CollectAsync(s, 700m);
        var disputedHandIn = await HandInAsync(s.Agent);
        (await s.Accountant.PostAsJsonAsync(SettlementUri(disputedHandIn.Id, "/verify"), new VerifySettlementRequest(650m, "Short by fifty"))).EnsureSuccessStatusCode();
        await CollectAsync(s, 300m); // still in the agent's hand
        Assert.NotEqual(Guid.Empty, settled.Id);

        var report = await ReadAsync<PaymentReportResponse>(await s.Accountant.GetAsync(new Uri($"{ReportUri}?branchId={s.Branch}", UriKind.Relative)));

        var paidTotal = esewa.Price.TotalCharge + cashParcel.Price.TotalCharge;
        Assert.Equal(2, report.PaidCount);
        Assert.Equal(paidTotal, report.PaidAmount);
        Assert.Equal(s.Branch, report.BranchId);
        Assert.Equal(["Cash", "Esewa"], report.ByMethod.Select(m => m.Method));
        Assert.Equal(cashParcel.Price.TotalCharge, report.ByMethod.Single(m => m.Method == "Cash").Amount);
        Assert.Equal(esewa.Price.TotalCharge, report.ByMethod.Single(m => m.Method == "Esewa").Amount);

        // Unpaid at the end of the period: the Khalti parcels and the three cash-on-delivery parcels the agent is carrying.
        Assert.Equal(1 + 1 + 3, report.PendingCount);
        Assert.Equal(1, report.FailedAttempts);
        var esewaRow = report.ByProvider.Single(p => p.Provider == "Esewa");
        Assert.Equal((1, 0, 0), (esewaRow.Succeeded, esewaRow.Failed, esewaRow.Pending));
        Assert.Equal(esewa.Price.TotalCharge, esewaRow.Amount);
        var khaltiRow = report.ByProvider.Single(p => p.Provider == "Khalti");
        Assert.Equal((0, 1, 0), (khaltiRow.Succeeded, khaltiRow.Failed, khaltiRow.Pending));
        Assert.Equal(0m, khaltiRow.Amount);
        Assert.Equal(1, report.ByProvider.Single(p => p.Provider == "Cash").Succeeded);

        Assert.Equal(30, report.Daily.Count);
        Assert.Equal(report.To, report.Daily[^1].Date);
        Assert.Equal(2, report.Daily[^1].Count);
        Assert.Equal(paidTotal, report.Daily[^1].Amount);
        Assert.Equal(0, report.Daily[0].Count);

        Assert.Equal(3, report.Cod.CollectionCount);
        Assert.Equal(1800m, report.Cod.Collected);
        Assert.Equal(300m, report.Cod.InHand);
        Assert.Equal(0m, report.Cod.InSettlement);
        Assert.Equal(700m, report.Cod.Disputed);
        Assert.Equal(800m, report.Cod.Settled);
        Assert.Equal(1, report.Settlements.Verified);
        Assert.Equal(1, report.Settlements.Disputed);
        Assert.Equal(0, report.Settlements.Submitted);
        Assert.Equal(-50m, report.Settlements.TotalVariance);
    }

    [Fact]
    public async Task TheSummaryReport_HasAPeriod_AScope_AndValidation()
    {
        var own = await _booking.CreateBranchAsync();
        var other = await _booking.CreateBranchAsync();
        var manager = await _booking.StaffAtAsync(Roles.BranchManager, own);
        var counter = await _booking.StaffAsync(Roles.BookingStaff);
        var parcel = await BookAsync(counter, NewBooking(own, payment: "Cash")); // paid at the counter
        await BookAsync(counter, NewBooking(other, payment: "Cash"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.75));
        string Day(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var mine = await ReadAsync<PaymentReportResponse>(await manager.GetAsync(ReportUri));
        var thisDay = await ReadAsync<PaymentReportResponse>(await manager.GetAsync(new Uri($"{ReportUri}?from={Day(today)}&to={Day(today)}", UriKind.Relative)));
        var future = await ReadAsync<PaymentReportResponse>(await manager.GetAsync(new Uri($"{ReportUri}?from={Day(today.AddDays(1))}&to={Day(today.AddDays(3))}", UriKind.Relative)));

        Assert.Equal(own, mine.BranchId);
        Assert.Equal(1, mine.PaidCount);
        Assert.Equal(parcel.Price.TotalCharge, mine.PaidAmount);
        Assert.Single(thisDay.Daily);
        Assert.Equal(0, future.PaidCount);
        Assert.Equal(3, future.Daily.Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(new Uri($"{ReportUri}?branchId={other}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _booking.StaffAsync(Roles.BranchManager)).GetAsync(ReportUri)).StatusCode);
        var accountant = await _booking.StaffAsync(Roles.Accountant);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync(ReportUri)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.GetAsync(new Uri($"{ReportUri}?from={Day(today)}&to={Day(today.AddDays(-1))}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.GetAsync(new Uri($"{ReportUri}?from={Day(today.AddDays(-400))}&to={Day(today)}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(ReportUri)).StatusCode);
    }
}
